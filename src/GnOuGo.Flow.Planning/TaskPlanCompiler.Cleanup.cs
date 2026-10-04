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

    private static void GuardCleanup(PlanningNode node)
    {
        var required = PlanningValues.And(PlanningValues.ReadGuard(node.Input), node.Expr is null ? null : PlanningValues.ReadGuard(node.Expr));
        if (required is not null) node.If = PlanningValues.And(required, node.If);
    }

}
