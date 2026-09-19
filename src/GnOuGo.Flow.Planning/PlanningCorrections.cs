using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

/// <summary>Ephemeral, engine-issued edit targets. No arbitrary paths or host fields can be changed.</summary>
internal static class PlanningCorrections
{
    internal sealed record Target(string Id, string Path, string Shape, JsonNode? Fragment);
    internal static IReadOnlyList<Target> Targets(PlanningSession state)
    {
        var intent = PlanningJsonTransport.Intent(state.IntentPlan!);
        if (state.PendingCall is { Purpose: "repair" } pending)
        {
            var original = JsonNode.Parse(pending.Request.Prompt[pending.Request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            return original["targets"]!.AsArray().Select(t => new Target(t!["id"]!.GetValue<string>(), t["path"]!.GetValue<string>(), t["shape"]!.GetValue<string>(), t["fragment"]?.DeepClone())).ToArray();
        }
        var operations = IntentTraversal.Located(state.IntentPlan!).ToArray();
        var targets = new List<Target>();
        foreach (var diagnostic in PlanningDiagnosticLocations.ForIntent(state).Where(d => d.Required))
        {
            var path = diagnostic.Location; string shape;
            if (diagnostic.ValidationStage == "fixtures")
            {
                var domain = PlanningFixtureSamples.Domains(state).FirstOrDefault(d => d.Path == path);
                if (domain.Schema is null) continue;
                var values = PlanningFixtureSamples.Values(state, path);
                targets.Add(new("target_" + PlanningGraphCompiler.Fingerprint(path)[..16], path, path == "/fixtures/inputs" ? "fixture_inputs" : "fixture_responses",
                    values is null ? null : path == "/fixtures/inputs" ? values[0]?.DeepClone() : new JsonArray(values.Select(v => v?.DeepClone()).ToArray())));
                continue;
            }
            var operation = operations.Where(o => path == o.Path || path.StartsWith(o.Path + "/", StringComparison.Ordinal)).OrderByDescending(o => o.Path.Length).FirstOrDefault();
            if (diagnostic.Code is "DEPENDENCY_CYCLE" or "WORKFLOW_CYCLE" || operation.Operation is null && !path.StartsWith("/inputs/", StringComparison.Ordinal) && !path.StartsWith("/outputs/", StringComparison.Ordinal) && !path.StartsWith("/subflows/", StringComparison.Ordinal))
            { path = operation.Operation is null ? "/operations" : operation.Path[..operation.Path.LastIndexOf("/operations/", StringComparison.Ordinal)] + "/operations"; shape = "operations"; }
            else if (operation.Operation is not null)
            {
                var suffix = path[operation.Path.Length..];
                if (suffix == "/arguments") shape = "members";
                else if (suffix == "/resultType") shape = "type";
                else if (suffix.Length > 0 && PlanningFieldPaths.ReadOptional(intent, path) is JsonObject value && value.ContainsKey("kind")) shape = "value";
                else { path = operation.Path; shape = "operation"; }
            }
            else
            {
                var segments = path.Split('/');
                var port = Array.FindIndex(segments, p => p is "inputs" or "outputs");
                if (port < 0 && segments.Length >= 3 && segments[1] == "subflows") { path = "/subflows/" + segments[2]; shape = "subflow"; }
                else if (port < 0 || port + 1 >= segments.Length) { path = "/operations"; shape = "operations"; }
                else { shape = segments[port] == "inputs" ? "input" : "output"; path = string.Join('/', segments.Take(port + 2)); }
            }
            if (PlanningFieldPaths.ReadOptional(intent, path) is not { } fragment) continue;
            targets.Add(new("target_" + PlanningGraphCompiler.Fingerprint(path)[..16], path, shape, fragment.DeepClone()));
        }
        // A group replacement subsumes nested edits. Atomic responses may not overlap.
        return targets.DistinctBy(t => t.Path).Where(t => !targets.Any(parent => parent.Path != t.Path && t.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal))).ToArray();
    }
    internal static JsonObject Schema(IReadOnlyList<Target> targets)
    {
        var variants = targets.Select(t => (JsonNode)new JsonObject {
            ["type"] = "object", ["properties"] = new JsonObject {
                ["target"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(t.Id) },
                ["replacement"] = t.Shape switch {
                    "operations" => new JsonObject { ["type"] = "array", ["items"] = Ref("operation") },
                    "members" => new JsonObject { ["type"] = "array", ["items"] = Ref("member") },
                    "fixture_inputs" => Ref("literal_object"), "fixture_responses" => Ref("literal_array"),
                    _ => Ref(t.Shape) } },
            ["required"] = new JsonArray("target", "replacement"), ["additionalProperties"] = false }).ToArray();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
            ["changes"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = targets.Count, ["items"] = new JsonObject { ["anyOf"] = new JsonArray(variants) } } },
            ["required"] = new JsonArray("changes"), ["additionalProperties"] = false, ["$defs"] = PlanningSchemas.Definitions() };
        var definitions = schema["$defs"]!.AsObject();
        definitions["literal"] = new JsonObject { ["anyOf"] = new JsonArray(PlanningSchemas.Definitions()["value"]!["anyOf"]!.AsArray().Take(4).Select(v => v!.DeepClone()).Concat([Ref("literal_object"), Ref("literal_array")]).ToArray()) };
        definitions["literal"]!["anyOf"]![0]!["properties"]!["kind"]!["enum"] = new JsonArray("null");
        definitions["literal_object"] = LiteralContainer("object", "members", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" }, ["value"] = Ref("literal") }, ["required"] = new JsonArray("name", "value"), ["additionalProperties"] = false });
        definitions["literal_array"] = LiteralContainer("array", "items", Ref("literal"));
        PlanningJsonTransport.PruneDefinitions(schema); return schema;
        static JsonObject LiteralContainer(string kind, string name, JsonObject item) => new() { ["type"] = "object", ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(kind) }, [name] = new JsonObject { ["type"] = "array", ["items"] = item } }, ["required"] = new JsonArray("kind", name), ["additionalProperties"] = false };
        static JsonObject Ref(string shape) => new() { ["$ref"] = "#/$defs/" + shape };
    }
    internal static string Prompt(PlanningSession state, IReadOnlyList<Target> targets)
    {
        var referenced = IntentTraversal.Operations(state.IntentPlan!.Operations.Concat(state.IntentPlan.Subflows.SelectMany(f => f.Operations)))
            .OfType<InvokeIntentOperation>().Select(o => o.Capability).ToHashSet(StringComparer.Ordinal);
        var query = string.Join(" ", state.Diagnostics.Select(d => d.Message));
        var relevant = state.Catalog!.Capabilities.Where(c => referenced.Contains(c.Id)).Concat(PlanningCapabilityCards.Shortlist(state.Catalog, query, state.Request.Generation.MaxInputTokensPerRequest)).DistinctBy(c => c.Id);
        var operations = IntentTraversal.Located(state.IntentPlan).ToArray();
        var blocks = IntentTraversal.Blocks(state.IntentPlan).ToArray();
        string Scope(string path) => blocks.Where(b => path.StartsWith(b.Path + "/operations/", StringComparison.Ordinal)).OrderByDescending(b => b.Path.Length).Select(b => b.Path).FirstOrDefault()
            ?? (path.StartsWith("/subflows/", StringComparison.Ordinal) ? string.Join('/', path.Split('/').Take(3)) : "/");
        string Placement((IntentOperation Operation, string Path) item) => item.Operation is CleanupIntentOperation ? "cleanup_group" :
            operations.Any(o => o.Operation is CleanupIntentOperation && item.Path.StartsWith(o.Path + "/operations/", StringComparison.Ordinal) && Scope(o.Path) == Scope(item.Path)) ? "finalizer" : "main";
        var intent = PlanningJsonTransport.Intent(state.IntentPlan);
        var diagnostics = PlanningDiagnosticLocations.ForIntent(state);
        var dependencyTargets = new JsonArray();
        foreach (var diagnostic in diagnostics.Where(d => d.Code is "DEPENDENCY_UNKNOWN" or "DEPENDENCY_SCOPE" or "DEPENDENCY_CYCLE"))
        {
            var item = operations.Where(o => diagnostic.Location == o.Path || diagnostic.Location.StartsWith(o.Path + "/", StringComparison.Ordinal)).OrderByDescending(o => o.Path.Length).FirstOrDefault();
            if (item.Operation is null || dependencyTargets.Any(t => t!["path"]!.ToString() == item.Path)) continue;
            var owner = IntentTraversal.GraphOwner(state.IntentPlan, item.Path);
            var workflow = (owner == "main" ? state.Graph!.Workflows.FirstOrDefault(w => w.Key == PlanningConfirmationGuards.Body) : null)
                ?? state.Graph!.Workflows.FirstOrDefault(w => w.Key == owner);
            var eligible = workflow is null ? [] : PlanningStructureValidation.EligibleDependencies(workflow, item.Operation.Id);
            var cleanup = operations.Where(o => o.Operation is CleanupIntentOperation && Scope(o.Path) == Scope(item.Path) && item.Operation.After.Contains(o.Operation.Id)).Select(o => o.Operation.Id).ToArray();
            dependencyTargets.Add((JsonNode)new JsonObject { ["operation"] = item.Operation.Id, ["path"] = item.Path, ["scope"] = Scope(item.Path), ["placement"] = Placement(item),
                ["eligibleAfter"] = new JsonArray(eligible.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                ["explanation"] = (cleanup.Length == 0 ? "" : "Referenced cleanup group " + string.Join(", ", cleanup) + " is not an executable predecessor. ") +
                    "These IDs are structurally eligible in the current graph, not business recommendations or permissions. Finalizers depending on a main operation require its completed result; adding such an edge can skip cleanup after failure. Empty after is valid when no extra sequencing is needed. All replacements are fully revalidated." });
        }
        return "Correct only the issued business intent targets. Return changes with target IDs and typed replacements. Do not return a complete intent. " +
            "The engine owns transport, result channels, catalog schemas and permissions. Keep business result references and named computation parameters. " +
            "Fix the exact diagnostics; unchanged replacements will stop. A group target permits topology changes. Fixture targets use recursive literal values only; never business references or computations.\n" + PlanningModelCalls.BusinessExamples + "\n" + new JsonObject {
                ["request"] = state.Request.Prompt,
                ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
                ["targets"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["path"] = t.Path, ["shape"] = t.Shape, ["fragment"] = t.Fragment?.DeepClone() }).ToArray()),
                ["bindings"] = new JsonObject { ["inputs"] = PlanningJsonTransport.Intent(state.IntentPlan)["inputs"]!.DeepClone(),
                    ["operations"] = new JsonArray(operations.Select(o => (JsonNode)new JsonObject { ["id"] = o.Operation.Id, ["purpose"] = o.Operation.Purpose,
                        ["kind"] = PlanningFieldPaths.Read(intent, o.Path)!["kind"]!.DeepClone(), ["scope"] = Scope(o.Path), ["placement"] = Placement(o) }).ToArray()) },
                ["dependencyTargets"] = dependencyTargets,
                ["fixtureContracts"] = new JsonArray(PlanningFixtureSamples.Domains(state).Where(d => targets.Any(t => t.Path == d.Path)).Select(d => (JsonNode)new JsonObject { ["path"] = d.Path, ["schema"] = d.Schema.DeepClone() }).ToArray()),
                ["contracts"] = new JsonArray(relevant.Select(c => (JsonNode)new JsonObject { ["card"] = PlanningCapabilityCards.Card(c), ["arguments"] = c.InputSchema.DeepClone(), ["result"] = c.OutputSchema.DeepClone() }).ToArray())
            }.ToJsonString();
    }
    internal static WorkflowIntentPlan Apply(PlanningSession state, IReadOnlyList<Target> targets, JsonNode response)
    {
        var updated = PlanningJsonTransport.Intent(state.IntentPlan!);
        var fixtures = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Fixtures ?? new(), PlanningJsonContext.Default.PlanningFixtures), PlanningJsonContext.Default.PlanningFixtures)!;
        var fixtureChanged = false; var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in response["changes"]!.AsArray())
        {
            var id = edit!["target"]!.GetValue<string>(); var target = targets.SingleOrDefault(t => t.Id == id);
            if (target is null || !changed.Add(id)) throw new PlanningResponseException([new("REPAIR_TARGET_INVALID", "/changes", "Select each issued target at most once.", ValidationStage: "intent")]);
            if (target.Shape.StartsWith("fixture_", StringComparison.Ordinal))
            {
                fixtureChanged = true;
                var literal = JsonSerializer.Deserialize(edit["replacement"], PlanningJsonContext.Default.PlanningValue)!;
                if (!PlanningGraphValidation.IsLiteral(literal)) throw new PlanningResponseException([new("SCENARIO_FIXTURE_INVALID", target.Path, "Only literal fixture values are accepted.", ValidationStage: "fixtures")]);
                var value = PlanningGraphValidation.Literal(literal);
                if (target.Shape == "fixture_inputs") fixtures.Inputs = value!.AsObject();
                else
                {
                    var parts = target.Path.Split('/'); var workflow = PlanningFixtureSamples.Unescape(parts[3]); var node = PlanningFixtureSamples.Unescape(parts[4]);
                    fixtures.Observations.RemoveAll(o => o.Workflow == workflow && o.Node == node);
                    fixtures.Observations.Add(new(workflow, node, value!.AsArray().Select(v => v?.DeepClone()).ToList()));
                }
            }
            else PlanningFieldPaths.Replace(updated, target.Path, edit["replacement"]);
        }
        var errors = PlanningContractValidation.ValidateInstanceFindings(updated, PlanningSchemas.Intent());
        if (errors.Count > 0) throw new PlanningResponseException(errors.Select(f => new PlanningDiagnostic("INTENT_SCHEMA_INVALID", f.InstancePointer, f.Message, ValidationStage: "intent")).ToList());
        var result = JsonSerializer.Deserialize(updated, PlanningJsonContext.Default.WorkflowIntentPlan)!;
        if (fixtureChanged) state.Fixtures = fixtures; return result;
    }
}
