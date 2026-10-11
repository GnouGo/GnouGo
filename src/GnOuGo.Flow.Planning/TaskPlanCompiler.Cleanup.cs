using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    // Snapshot optional ancestor results without eagerly reading an absent payload.
    // The switch envelope omits its unselected branch; absence is never business null.
    private Bound CaptureFailureOutput(TaskValue reference, Scope scope, bool consume)
    {
        var owner = scope.Parent;
        while (owner is not null && !owner.Tasks.ContainsKey(reference.Source!)) owner = owner.Parent;
        if (owner is null) Fail("TASK_REFERENCE_UNKNOWN", "Business reference '" + reference.Source + "' is unavailable in this scope.");
        var original = Value(reference, owner!, consume: false);
        var whole = Value(new() { Kind = "output", Source = reference.Source }, owner!, consume: false);
        var presentKey = Key(owner!.Workflow.Key, "capture:" + reference.Source + ":present");
        var captured = Envelope(scope);
        var path = original.SelectionPath ?? original.Value.Path.Skip(whole.Value.Path.Count).ToList();
        var selected = original with { SelectionSource = captured, SelectionPath = [presentKey, "value", .. path] };
        return consume ? Consume(selected, scope) : selected;

        Bound Envelope(Scope current)
        {
            if (current.FailureCaptures.TryGetValue(reference.Source!, out var existing)) return existing;
            Bound result;
            if (ReferenceEquals(current, owner))
            {
                var key = Key(current.Workflow.Key, "capture:" + reference.Source);
                var absentKey = Key(key, "absent");
                var presentSchema = ObjectSchema([("value", whole.Schema)]);
                var emptySchema = ObjectSchema([]);
                var presentEnvelope = ObjectSchema([(presentKey, presentSchema)]);
                presentEnvelope["required"] = new JsonArray(); presentEnvelope.Remove("additionalProperties");
                var absentEnvelope = ObjectSchema([(absentKey, emptySchema)]); absentEnvelope.Remove("additionalProperties");
                var schema = new JsonObject { ["anyOf"] = new JsonArray(presentEnvelope, absentEnvelope) };
                var presence = new PlanningValue { Kind = "present", Source = whole.Value.Source };
                current.Target?.Add(new() { Key = key, Type = "switch", Expr = presence,
                    Cases = [new("true", null, [new() { Key = presentKey, Type = "set", Input = Object([new("value", whole.Value)]),
                        If = presence, OutputSchema = Contract(presentSchema) }])],
                    Default = [new() { Key = absentKey, Type = "set", Input = Object([]), OutputSchema = Contract(emptySchema) }] });
                _sources[key] = _location;
                result = Output(key, "switch", [], schema);
            }
            else
            {
                var parent = Envelope(current.Parent!);
                if (ReferenceEquals(current.Workflow, current.Parent!.Workflow)) result = parent;
                else
                {
                    var name = Key("capture", reference.Source!);
                    current.Workflow.Inputs.Add(new() { Name = name, Schema = Contract(parent.Schema) });
                    current.Captures.Add(new(name, parent.Value));
                    result = Input(name, parent.Schema);
                }
            }
            current.FailureCaptures[reference.Source!] = result;
            return result;
        }
    }

    // Only exports depending on this workflow's finalizers belong after them.
    // Captured ancestors are already available inputs of the current scope.
    private void CompileExports(TaskScope source, Scope scope, string key)
    {
        var finalizers = PlanningGraphCompiler.Enumerate(scope.Workflow.Finally).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var normal = new List<(string Name, Bound Bound, string Location, bool Copy)>();
        var checks = new List<PlanningNode>();
        foreach (var output in source.Outputs)
        {
            var pending = new List<PlanningNode>();
            scope.Target = pending; scope.Cleanup = false;
            _location = (_sources.GetValueOrDefault(key) ?? "/root") + "/outputs/" + output.Name;
            var location = _location;
            var bound = Value(output.Value, scope);
            var afterCleanup = PlanningGraphTopology.ReferencedStages(bound.Value)
                .Concat(pending.SelectMany(PlanningGraphTopology.References).SelectMany(PlanningGraphTopology.ReferencedStages))
                .Any(finalizers.Contains);
            _sources[key + "/outputs/" + scope.Workflow.Outputs.Count] = location;
            if (afterCleanup)
            {
                foreach (var node in PlanningGraphCompiler.Enumerate(pending)) GuardCleanup(node);
                scope.Workflow.Finally.AddRange(pending);
                if (bound.Value.Kind is "object" or "array")
                {
                    var export = Key(key, "export:" + output.Name); _sources[export] = location;
                    var node = new PlanningNode { Key = export, Type = "set", Purpose = "Export results produced during finalization",
                        Input = Object([new("value", bound.Value)]), OutputSchema = Contract(ObjectSchema([("value", bound.Schema)])) };
                    GuardCleanup(node); scope.Workflow.Finally.Add(node);
                    bound = Output(export, "set", ["value"], bound.Schema);
                }
            }
            else if (pending.Count != 0 || bound.Value.Kind is "object" or "array")
            {
                checks.AddRange(pending);
                normal.Add((output.Name, bound, location, CopyValue(output.Value)));
                bound = Output(Key(key, "exports"), "set", [output.Name], bound.Schema);
            }
            scope.Workflow.Outputs.Add(new() { Name = output.Name, Value = bound.Value, Schema = Contract(bound.Schema) });
        }
        if (normal.Count == 0) return;
        var grouped = Key(key, "exports");
        // These private checks are created only for these outputs. Copy bindings
        // retain their exact contracts as members of the assembled result.
        var fuse = normal.All(o => o.Copy) && checks.All(n => n.Type == "set" && n.Input.Kind == "projection" && n.If is null);
        if (fuse) foreach (var check in checks) check.InternalRole = "inline:" + grouped;
        scope.Workflow.Steps.AddRange(checks);
        scope.Workflow.Steps.Add(new() { Key = grouped, Type = "set", Purpose = "Export declared business results",
            InternalRole = fuse ? "typed_assembly" : null,
            Input = Object(normal.Select(o => new PlanningMember(o.Name, o.Bound.Value))),
            OutputSchema = Contract(ObjectSchema(normal.Select(o => (o.Name, o.Bound.Schema)))) });
        _sources[grouped] = normal[0].Location;
        for (var i = 0; i < normal.Count; i++) _sources[grouped + "/input/members/" + i + "/value"] = normal[i].Location;
    }

    private static string TechnicalDescription(PlanningNode node) => node.Type switch
    {
        "set" when node.Input.Kind == "projection" && PlanningGraphValidation.Member(node.Input, "each")?.Boolean == true => "Collect declared iteration outputs",
        "set" when node.Input.Kind == "projection" => "Select and validate declared values",
        "set" => "Assemble declared values",
        "workflow.call" => "Execute scoped workflow",
        "switch" => "Select the declared branch",
        "template.render" => "Format the request from declared inputs",
        _ => "Execute " + node.Type
    };

    private static void GuardCleanup(PlanningNode node)
    {
        var required = PlanningValues.And(PlanningValues.ReadGuard(node.Input), node.Expr is null ? null : PlanningValues.ReadGuard(node.Expr));
        if (required is not null) node.If = PlanningValues.And(required, node.If);
    }

}
