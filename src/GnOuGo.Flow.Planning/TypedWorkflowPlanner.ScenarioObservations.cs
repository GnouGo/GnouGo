using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    // Observation sequences are private test fixtures, never executable defaults.
    // Static schema samples cannot model an external continuation that changes over time.
    private async Task<bool> PrepareScenarioObservationsAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workflow in state.Graph!.Workflows)
        foreach (var loop in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n =>
                     n.Type == "loop.sequential" && n.Input.Members.Any(m => m.Name == "while")))
        foreach (var node in PlanningGraphCompiler.Enumerate(loop.Steps).Where(n => n.Type == "mcp.call"))
        {
            var key = (workflow.Key == state.Graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..16]) + ":n_" + PlanningGraphCompiler.Fingerprint(node.Key)[..16];
            retained.Add(key);
            var contract = PlanningGraphValidation.ResolveValueContract(state.Graph, workflow, new() { Kind = "output", Source = node.Key, ResultChannel = "envelope" }, state.Preparation!);
            var context = JsonSerializer.Serialize(loop, PlanningJsonContext.Default.PlanningNode);
            var fingerprint = PlanningGraphCompiler.Fingerprint(context + contract.ToJsonString());
            if (state.ScenarioObservations[key] is JsonObject cached && cached["fingerprint"]?.ToString() == fingerprint &&
                cached["responses"] is JsonArray existing && existing.Count is >= 2 and <= 3 &&
                (cached["totalObservations"] is null || cached["totalObservations"]!.GetValue<int>() == existing.Count) && existing.All(s => PlanningContractValidation.ValidateInstance(s, contract).Count == 0)) continue;
            var samples = new JsonArray(); var total = 0;
            if (state.ScenarioObservations[key] is JsonObject partial && partial["fingerprint"]?.ToString() == fingerprint &&
                partial["totalObservations"] is JsonValue count && count.TryGetValue<int>(out var expected) && expected is 2 or 3 &&
                partial["responses"] is JsonArray retainedSamples && retainedSamples.Count < expected &&
                retainedSamples.All(s => PlanningContractValidation.ValidateInstance(s, contract).Count == 0))
            { samples = (JsonArray)retainedSamples.DeepClone(); total = expected; }
            var definitions = PlanningSchemas.Graph(state.Preparation!)["$defs"]!.DeepClone().AsObject();
            var variants = definitions["value"]!["anyOf"]!.AsArray();
            foreach (var variant in variants.ToArray())
            {
                var kinds = variant!["properties"]!["kind"]!["enum"]!.AsArray();
                foreach (var kind in kinds.ToArray()) if (kind?.ToString() is not ("string" or "number" or "boolean" or "null" or "object" or "array")) kinds.Remove(kind);
                if (kinds.Count == 0) variants.Remove(variant);
            }
            var compactContext = PlanningConstructionSchemas.Compact(PlanningModelValues.Compact(JsonSerializer.SerializeToNode(loop, PlanningJsonContext.Default.PlanningNode))!.AsObject()).ToJsonString();
            var prompt = "Construct one observation in an explicit synthetic sequence of 2 or 3 successful observations for the named MCP step in this loop. " +
                "Use a typed literal matching its complete result envelope, keeping raw response and structured json separate. " +
                "The complete sequence must exercise continuation and then completion according to the producer contract and loop expressions. " +
                "Choose the smallest array cardinalities needed by those expressions; a numeric request limit alone does not require that many fixture records. " +
                "Do not duplicate structured data into an opaque raw result unless the loop consumes it. Preserve valid encoded artifact fields. " +
                "These are deterministic test data, never live evidence. Do not change the workflow or model human consent. " +
                "Execution must consume the entire sequence and stop without requesting another observation. " +
                "Return only the requested observation, not the previous or remaining ones.\nTarget: " + node.Key +
                "\nResult contract:\n" + contract.ToJsonString() + "\nLoop under test:\n" + compactContext;
            while (total == 0 || samples.Count < total)
            {
                var shape = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("responses", "totalObservations"),
                    ["properties"] = new JsonObject {
                        ["totalObservations"] = new JsonObject { ["type"] = "integer", ["enum"] = total == 0 ? new JsonArray(2, 3) : new JsonArray(total) },
                        ["responses"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 1, ["items"] = new JsonObject { ["$ref"] = "#/$defs/value" } } },
                    ["$defs"] = definitions.DeepClone() };
                PlanningConstruction.PruneDefinitions(shape);
                var findings = new List<PlanningDiagnostic>(); JsonObject? rejected = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var actual = prompt + "\nObservation number: " + (samples.Count + 1) + "\nAccepted earlier observations:\n" + samples.ToJsonString() +
                        (total == 0 ? "\nChoose totalObservations as 2 or 3; this first observation must continue." : "\nLocked totalObservations: " + total + (samples.Count + 1 == total ? "; this final observation must terminate." : "; this observation must continue.")) +
                        (findings.Count == 0 ? "" : "\nRepair this observation only:\n" + rejected?.ToJsonString() + "\nFixture diagnostics:\n" + JsonSerializer.Serialize(findings, PlanningJsonContext.Default.ListPlanningDiagnostic));
                    if (PlanningConstruction.EstimateInputTokens(actual, shape) > state.Request.Generation.MaxInputTokensPerUnit)
                    { findings = [new("SCENARIO_OBSERVATION_CONTEXT_TOO_LARGE", key, "The explicit observation fixture exceeds the configured context ceiling; no request was dispatched.")]; break; }
                    JsonObject response;
                    try { response = await StructuredAsync(state, runtime, "scenario_observations", actual, shape, ct, maxAttempts: 1, checkpoint: true); }
                    catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException ex) when (ex.Code == GnOuGo.Flow.Core.Models.ErrorCodes.LlmSchema)
                    { findings = [new("SCENARIO_OBSERVATION_INVALID", key, "The synthetic observation has an invalid literal shape.")]; continue; }
                    rejected = response; findings.Clear();
                    var value = JsonSerializer.Deserialize(response["responses"]![0]!, PlanningJsonContext.Default.PlanningValue)!;
                    if (!PlanningGraphValidation.IsLiteral(value)) { findings.Add(new("SCENARIO_OBSERVATION_INVALID", key, "Observations must be literal values.")); continue; }
                    var sample = PlanningGraphValidation.Literal(value);
                    findings.AddRange(PlanningContractValidation.ValidateInstance(sample, contract).Select(error => new PlanningDiagnostic("SCENARIO_OBSERVATION_INVALID", key, error)));
                    if (findings.Count != 0) continue;
                    total = response["totalObservations"]!.GetValue<int>(); samples.Add(sample);
                    state.ScenarioObservations[key] = new JsonObject { ["fingerprint"] = fingerprint, ["schema"] = contract.DeepClone(), ["responses"] = samples.DeepClone(), ["totalObservations"] = total };
                    state.BestScenarios.Clear();
                    await runtime.CheckpointAsync(state, ct); break;
                }
                if (findings.Count != 0) { state.Diagnostics = findings; state.CurrentPhase = "scenario_observations"; state.Status = PlanningStatus.Recovery; return false; }
            }
        }
        foreach (var key in state.ScenarioObservations.Select(p => p.Key).Where(k => !retained.Contains(k)).ToArray()) state.ScenarioObservations.Remove(key);
        return true;
    }
}
