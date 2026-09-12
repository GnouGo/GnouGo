using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningSemanticReview
{

    internal async Task<List<PlanningDiagnostic>> ReviewAsync(PlanningSnapshot state, PlanningGraph graph, IPlanningRuntime runtime, CancellationToken ct)
    {
        var targets = SemanticTargets(graph, state.Preparation!);
        var sources = PlanningIntentAssessment.IntentSources(state);
        var sourceMap = sources.ToDictionary(s => s.Id, s => s.Text, StringComparer.Ordinal);
        var references = sources.SelectMany(s => PlanningReferences.Register(state, s.Id, s.Kind, s.Text))
            .Where(r => !string.IsNullOrWhiteSpace(sourceMap[r.SourceId].Substring(r.Start, r.Length))).ToArray();
        var indexed = PlanningSemanticContext.Executable(graph);
        var decisions = new List<PlanningDecisionPages.Decision>();
        var coordinates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, target) in targets)
            coordinates["target_" + PlanningGraphCompiler.Fingerprint(PlanningFieldPaths.Canonical(PlanningFieldPaths.Json(graph), path))[..20]] = path;
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var root = "/workflows/" + wi;
            var nodes = indexed["workflows"]![wi]!["nodes"]!.AsObject();
            var locations = nodes.Select(p => (Key: p.Key, Path: p.Value!["location"]!.ToString())).ToArray();
            var units = locations.Append((Key: "$workflow", Path: root)).ToArray();
            foreach (var unit in units)
            {
                var scoped = coordinates.Where(p => p.Value.StartsWith(unit.Path + "/", StringComparison.Ordinal) &&
                    !locations.Any(child => child.Path != unit.Path && child.Path.StartsWith(unit.Path + "/", StringComparison.Ordinal) && p.Value.StartsWith(child.Path + "/", StringComparison.Ordinal)))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                if (scoped.Count == 0) continue;
                var current = unit.Key == "$workflow" ? new JsonObject
                {
                    ["inputs"] = indexed["workflows"]![wi]!["inputs"]?.DeepClone(), ["outputs"] = indexed["workflows"]![wi]!["outputs"]?.DeepClone(),
                    ["steps"] = indexed["workflows"]![wi]!["steps"]?.DeepClone(), ["finally"] = indexed["workflows"]![wi]!["finally"]?.DeepClone(),
                    ["operations"] = new JsonObject(PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Select(n => new KeyValuePair<string, JsonNode?>(n.Key, JsonValue.Create(n.Purpose))))
                } : nodes[unit.Key]!.DeepClone().AsObject();
                var capabilityId = current["capabilityId"]?.ToString();
                var capability = state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == capabilityId);
                var obligations = capability?.OperationIds.ToHashSet(StringComparer.Ordinal) ?? [];
                var relevantIds = state.Obligations.Where(o => obligations.Contains(o.Id) || o.Owner == "workflow").SelectMany(o => o.EvidenceReferences).ToHashSet(StringComparer.Ordinal);
                var evidence = relevantIds.Count == 0 ? references : state.References.Where(r => relevantIds.Contains(r.Id) && sourceMap.ContainsKey(r.SourceId) && r.SourceFingerprint == PlanningGraphCompiler.Fingerprint(sourceMap[r.SourceId])).ToArray();
                foreach (var chunk in evidence.Chunk(8))
                {
                    var schema = new JsonObject { ["anyOf"] = new JsonArray(
                        PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum("passed"))),
                        PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum("finding")),
                            ("target", PlanningHoleRequests.Enum(scoped.Keys.ToArray())),
                            ("rule", PlanningHoleRequests.Enum("coverage", "ordering", "cardinality", "confirmation", "cleanup", "value_semantics", "contract")),
                            ("evidence", PlanningReferences.Schema(chunk)),
                            ("message", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 }))) };
                    var context = new JsonObject
                    {
                        ["task"] = "Assess this executable unit against the referenced intent. Select one concrete blocking defect or passed. Targets are exact coordinator coordinates; behavior targets mean governing changes requiring human review. Absence outside this unit is not a defect. Respect declared contracts, ownership, original-artifact provenance and runtime discovery. Do not infer undeclared result fields or facts from names.",
                        ["current"] = current.DeepClone(), ["targets"] = new JsonObject(scoped.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))),
                        ["evidence"] = new JsonObject(chunk.Select(r => new KeyValuePair<string, JsonNode?>(r.Id, JsonValue.Create(PlanningReferences.Resolve(state, r.Id, sourceMap))))),
                        ["ancestors"] = new JsonArray(locations.Where(n => unit.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)).Select(n => nodes[n.Key]!.DeepClone()).ToArray()),
                        ["contract"] = capability is null || capability.Resolution == "local" ? null : new JsonObject { ["input"] = capability.InputSchema.DeepClone(), ["output"] = capability.OutputSchema.DeepClone() },
                        ["valueContracts"] = ScopedValueContracts(graph, workflow, unit.Key, state.Preparation!)
                    };
                    var id = "review_" + PlanningGraphCompiler.Fingerprint(workflow.Key + ":" + unit.Key + ":" + string.Join("|", chunk.Select(r => r.Id)))[..24];
                    decisions.Add(new(id, schema, context, PlanningGraphCompiler.Fingerprint(current.ToJsonString() + PlanningContext.Contracts(state))));
                }
            }
        }
        var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "semantic_review", "$plan", decisions, ct);
        var diagnostics = new List<PlanningDiagnostic>();
        foreach (var (_, finding) in response)
        {
            if (finding!["status"]!.ToString() == "passed") continue;
            var location = coordinates[finding["target"]!.ToString()];
            var rule = finding["rule"]!.ToString();
            diagnostics.Add(new("SEMANTIC_" + rule.ToUpperInvariant(), location, finding["message"]!.ToString(), Rule: finding["evidence"]!.ToString()));
        }
        state.Validation.Assessment = new();
        return diagnostics.DistinctBy(d => (d.Code, d.Location, d.Rule)).ToList();
    }

    internal static JsonObject SemanticCapabilities(PlanningGraph graph, PlanningPreparation preparation)
    {
        var owned = graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally)))
            .Select(n => n.CapabilityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var schemas = new JsonObject(); var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject Reference(JsonObject schema)
        {
            var fingerprint = schema.ToJsonString();
            if (!identities.TryGetValue(fingerprint, out var id))
            { id = "c" + identities.Count; identities[fingerprint] = id; schemas[id] = schema.DeepClone(); }
            return new() { ["$ref"] = "#/schemas/" + id };
        }
        var capabilities = new JsonArray(preparation.Capabilities.Where(c => owned.Contains(c.Id)).Select(c =>
        {
            var contract = new JsonObject { ["id"] = c.Id, ["stepType"] = c.StepType, ["resolution"] = c.Resolution };
            // A local operation uses the graph's established typed value contracts.
            // The native executor's generic definition is not a business-output schema.
            if (c.Resolution != "local")
            {
                contract["inputSchema"] = Reference(c.InputSchema);
                contract["outputSchema"] = Reference(c.OutputSchema);
            }
            if (c.FixedInput.Count > 0) contract["fixedInput"] = c.FixedInput.DeepClone();
            if (c.RequestBindings.Count > 0)
                contract["requestBindings"] = new JsonArray(c.RequestBindings.Select(b => (JsonNode)new JsonObject { ["path"] = b.Path, ["value"] = b.Value?.DeepClone() }).ToArray());
            return (JsonNode)contract;
        }).ToArray());
        return new() { ["capabilities"] = capabilities, ["schemas"] = schemas };
    }

    internal sealed class SemanticAssessmentException(List<PlanningDiagnostic> diagnostics) : Exception
    { internal List<PlanningDiagnostic> Diagnostics { get; } = diagnostics; }

    internal static Dictionary<string, (string Workflow, bool Behavior)> SemanticTargets(PlanningGraph graph, PlanningPreparation preparation)
    {
        var targets = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        for (var i = 0; i < graph.Workflows.Count; i++)
        {
            var workflow = graph.Workflows[i]; var root = "/workflows/" + i;
            targets[root + "/behavior"] = (workflow.Key, true);
            var fields = new List<PlanningDiagnostic>();
            for (var pi = 0; pi < workflow.Inputs.Count; pi++)
                fields.Add(new("scope", root + "/inputs/" + pi + "/default", ""));
            for (var pi = 0; pi < workflow.Outputs.Count; pi++)
                fields.Add(new("scope", root + "/outputs/" + pi + "/value", ""));
            foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, root + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, root + "/finally")))
            {
                targets[path + "/behavior"] = (workflow.Key, true);
                if (node.CapabilityId is { } capabilityId && preparation.Capabilities.Any(c => c.Id == capabilityId && c.Resolution != "local"))
                    targets[path + "/preparation"] = (workflow.Key, false);
                if (node.InternalRole is not null) continue;
                foreach (var field in new[] { "input", "expr" }) fields.Add(new("scope", path + "/" + field, ""));
            }
            foreach (var target in PlanningPatches.Targets(graph, PlanningPatches.Scope(graph, fields)))
                targets[target.Path] = (workflow.Key, false);
        }
        return targets;
    }
}
