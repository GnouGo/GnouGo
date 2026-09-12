using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Freezes executable topology before any executable model request.</summary>
internal static class PlanningGraphSkeleton
{
    internal const string Unresolved = "unresolved";
    internal static void Create(PlanningSnapshot state)
    {
        var graph = PlanningBehaviorPlans.Display(state.BehaviorPlan!, state.Preparation!);
        state.Graph = graph; state.Construction.Holes.Clear(); state.Construction.Candidates.Clear();
        foreach (var workflow in graph.Workflows)
        {
            var wi = graph.Workflows.IndexOf(workflow); var root = "/workflows/" + wi;
            var boundaryProducers = state.Preparation!.Capabilities.ToDictionary(PlanningBehaviorDecisions.ResultPort, StringComparer.Ordinal);
            var baseline = state.Request.Baseline?.Workflows.SingleOrDefault(w => w.Key == workflow.Key);
            foreach (var port in workflow.Inputs)
            {
                if (boundaryProducers.TryGetValue(port.Name, out var boundary))
                {
                    RequireBoundaryContract(boundary, root + "/inputs/" + workflow.Inputs.IndexOf(port));
                    port.Schema = PlanningSchemaPropagation.Established(boundary.OutputSchema) ? new() { CapabilityId = boundary.Id, SchemaPointer = "/output" } : new() { Type = Unresolved };
                    if (port.Schema.Type == Unresolved) Add(state, workflow, null, root + "/inputs/" + workflow.Inputs.IndexOf(port) + "/schema", "schema", boundary.Description);
                    continue;
                }
                var previous = RevisedPort(state, workflow.Key, "inputs", port.Name) ? null : baseline?.Inputs.SingleOrDefault(p => p.Name == port.Name);
                port.Schema = previous?.Schema ?? new() { Type = Unresolved };
                port.Default = previous?.Default;
                if (previous is null) Add(state, workflow, null, root + "/inputs/" + workflow.Inputs.IndexOf(port) + "/schema", "schema",
                    state.BehaviorPlan!.Workflows.Single(w => w.Key == workflow.Key).Inputs.Single(p => p.Name == port.Name).Description);
                if (!port.Required && previous is null)
                { port.Default = new() { Kind = Unresolved }; Add(state, workflow, null, root + "/inputs/" + workflow.Inputs.IndexOf(port) + "/default", "default", "Default for input " + port.Name + ": " + state.BehaviorPlan!.Workflows.Single(w => w.Key == workflow.Key).Inputs.Single(p => p.Name == port.Name).Description); }
            }
            foreach (var port in workflow.Outputs)
            {
                var path = root + "/outputs/" + workflow.Outputs.IndexOf(port);
                if (boundaryProducers.TryGetValue(port.Name, out var boundary))
                {
                    RequireBoundaryContract(boundary, path);
                    port.Schema = PlanningSchemaPropagation.Established(boundary.OutputSchema) ? new() { CapabilityId = boundary.Id, SchemaPointer = "/output" } : new() { Type = Unresolved };
                    if (port.Schema.Type == Unresolved) Add(state, workflow, null, path + "/schema", "schema", boundary.Description);
                    var producers = workflow.Steps.Where(n => n.OperationIds.Intersect(boundary.OperationIds, StringComparer.Ordinal).Any()).ToArray();
                    if (producers.Length != 1)
                        throw new PlanningHoleUnavailableException(path, "The call boundary requires an established unconditional result projection. A model cannot invent a producer contract or change its availability.");
                    port.Value = new() { Kind = "output", Source = producers[0].Key };
                    continue;
                }
                port.Schema = (RevisedPort(state, workflow.Key, "outputs", port.Name) ? null : baseline?.Outputs.SingleOrDefault(p => p.Name == port.Name)?.Schema) ?? new() { Type = Unresolved };
                port.Value = new() { Kind = Unresolved };
                if (port.Schema.Type == Unresolved) Add(state, workflow, null, path + "/schema", "schema", state.BehaviorPlan!.Workflows.Single(w => w.Key == workflow.Key).Outputs.Single(p => p.Name == port.Name).Description);
                Add(state, workflow, null, path + "/value", "value", "Public output " + port.Name);
            }
            PlanningInternalAdapters.Insert(workflow, state.BehaviorPlan!.Workflows.Single(w => w.Key == workflow.Key));
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                if (IsAdapter(node)) continue;
                var capability = state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId);
                if (capability is null && node.CapabilityId is null)
                {
                    var declared = state.Preparation.Capabilities.Where(c => c.Resolution == "local" && c.StepType == node.Type &&
                        c.OperationIds.Count > 0 && c.OperationIds.ToHashSet(StringComparer.Ordinal).SetEquals(node.OperationIds)).ToArray();
                    if (declared.Length == 1) capability = declared[0];
                }
                if (PlanningProducerContracts.RequiresStructuredResult(node, state.Preparation))
                {
                    node.StructuredOutput = new(new() { Type = Unresolved });
                    Add(state, workflow, node, path + "/structuredOutput/schema", "schema", "Validated structured result consumed downstream: " + node.Purpose);
                }
                if (node.Type == "switch")
                {
                    node.Expr = new() { Kind = Unresolved };
                    Add(state, workflow, node, path + "/expr", "value", node.Purpose,
                        new() { ["type"] = "string", ["enum"] = new JsonArray(state.BehaviorPlan!.Workflows.Single(w => w.Key == workflow.Key).Steps
                            .Concat(state.BehaviorPlan.Workflows.Single(w => w.Key == workflow.Key).Finally)
                            .SelectMany(n => PlanningBehaviorPlans.Enumerate([n])).Single(n => n.Key == node.Key).Outcomes.Select(o => (JsonNode?)JsonValue.Create(o.Key)).ToArray()) });
                }
                else if (node.Type is "loop.sequential" or "loop.parallel")
                {
                    node.ItemVar = "item_" + PlanningGraphCompiler.Fingerprint(node.Key)[..12];
                    node.IndexVar = "index_" + PlanningGraphCompiler.Fingerprint(node.Key)[..12];
                    node.Input.Members.Add(new("items", new() { Kind = Unresolved }));
                    Add(state, workflow, node, path + "/input/members/0/value", "value", node.Purpose, new() { ["type"] = "array" });
                }
                else if (node.Type == "workflow.call")
                {
                    var target = graph.Workflows.Single(w => w.Key == PlanningWorkflowProvenance.Target(node));
                    var args = new PlanningValue { Kind = "object" };
                    node.Input.Members.Add(new("args", args));
                    foreach (var port in target.Inputs)
                    {
                        args.Members.Add(new(port.Name, new() { Kind = Unresolved }));
                        Add(state, workflow, node, path + "/input/members/1/value/members/" + (args.Members.Count - 1) + "/value", "value", "Callee argument " + target.Key + "." + port.Name);
                    }
                }
                else if (node.Type is not ("sequence" or "parallel"))
                {
                    PlanningSkeletonInputs.Build(state, workflow, node, path, capability);
                    if (node.Type == "set")
                    {
                        node.OutputSchema = capability is not null && PlanningSchemaPropagation.Established(capability.OutputSchema) ? new() { CapabilityId = capability.Id, SchemaPointer = "/output" } : new() { Type = Unresolved };
                        if (node.OutputSchema.Type == Unresolved) Add(state, workflow, node, path + "/outputSchema", "schema", node.Purpose + "; result contract");
                    }
                }
            }
            PlanningSkeletonInputs.GuardFinalizers(workflow, state.Preparation!);
        }
        foreach (var hole in state.Construction.Holes)
        {
            hole.CanonicalLocation = PlanningFieldPaths.Canonical(PlanningFieldPaths.Json(graph), hole.Path);
            hole.Id = "h_" + PlanningGraphCompiler.Fingerprint(hole.CanonicalLocation)[..16];
        }
        state.Construction.SkeletonFingerprint = Fingerprint(graph);

        static void RequireBoundaryContract(PlanningCapability capability, string path)
        {
            if (capability.Resolution != "local" && !PlanningSchemaPropagation.Established(capability.OutputSchema))
                throw new PlanningHoleUnavailableException(path, "The producer crossing this workflow boundary has no authoritative result contract. A model cannot establish an opaque external result.");
        }
    }

    internal static void Add(PlanningSnapshot state, PlanningWorkflow workflow, PlanningNode? node, string path, string kind, string purpose, JsonObject? expected = null)
    {
        state.Construction.Holes.Add(new()
        {
            Id = "h_" + PlanningGraphCompiler.Fingerprint(PlanningFieldPaths.Canonical(PlanningFieldPaths.Json(state.Graph!), path))[..16],
            WorkflowKey = workflow.Key, NodeKey = node?.Key, Path = path, CanonicalLocation = PlanningFieldPaths.Canonical(PlanningFieldPaths.Json(state.Graph!), path), Kind = kind, Purpose = purpose,
            ExpectedSchema = expected?.DeepClone().AsObject()
        });
    }
    internal static bool IsAdapter(PlanningNode node) => node.InternalRole is not null;
    private static bool RevisedPort(PlanningSnapshot state, string workflow, string collection, string port)
    {
        var path = "/workflows/" + PlanningFieldPaths.Escape("@" + workflow) + "/" + collection + "/" + PlanningFieldPaths.Escape("@" + port);
        return state.BehaviorRevision?.Fields.Any(f => f.CanonicalLocation == path || f.CanonicalLocation.StartsWith(path + "/", StringComparison.Ordinal)) == true;
    }
    internal static bool HasUnresolved(JsonNode? node) => node switch
    {
        JsonObject obj => obj["kind"]?.ToString() == Unresolved || obj["type"]?.ToString() == Unresolved || obj.Any(p => HasUnresolved(p.Value)),
        JsonArray array => array.Any(HasUnresolved),
        _ => false
    };
    internal static string Fingerprint(PlanningGraph graph)
    {
        var json = PlanningFieldPaths.Json(graph);
        foreach (var workflow in json["workflows"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var port in workflow["inputs"]!.AsArray().OfType<JsonObject>()) { port.Remove("schema"); port.Remove("default"); }
            foreach (var port in workflow["outputs"]!.AsArray().OfType<JsonObject>()) { port.Remove("schema"); port.Remove("value"); }
            void Nodes(JsonArray nodes)
            {
                foreach (var node in nodes.OfType<JsonObject>())
                {
                    if (node["internalRole"] is not null) continue;
                    var input = node["input"];
                    node["callee"] = node["type"]?.ToString() == "workflow.call" ? input?["members"]?.AsArray().Single(m => m?["name"]?.ToString() == "ref")?["value"]?.DeepClone() : null;
                    foreach (var field in new[] { "input", "expr", "outputSchema", "structuredOutput" }) node.Remove(field);
                    Nodes(node["steps"]!.AsArray()); Nodes(node["default"]!.AsArray());
                    foreach (var branch in node["branches"]!.AsArray()) Nodes(branch!["steps"]!.AsArray());
                    foreach (var outcome in node["cases"]!.AsArray()) { outcome!.AsObject().Remove("when"); Nodes(outcome["steps"]!.AsArray()); }
                }
            }
            Nodes(workflow["steps"]!.AsArray()); Nodes(workflow["finally"]!.AsArray());
        }
        return PlanningGraphCompiler.Fingerprint(json.ToJsonString());
    }
}
