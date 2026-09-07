using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TypedWorkflowPlanner
{
    internal static bool PreservesScenarioProgress(IReadOnlyList<PlanningScenarioResult> previous, IReadOnlyList<PlanningScenarioResult> current)
    {
        if (previous.Count == 0 || current.Count == 0) return false;
        var all = previous.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (!all.SetEquals(current.Select(s => s.Id))) return false;
        var passed = previous.Where(s => s.Outcome == "passed").Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return passed.IsProperSubsetOf(current.Where(s => s.Outcome == "passed").Select(s => s.Id));
    }

    // Validation fixtures are private planning evidence. They never become runtime
    // defaults or literal arguments and cannot alter the approved executable behavior.
    private async Task<bool> PrepareScenarioInputsAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var workflow = state.Graph!.Workflows.Single(w => w.Key == state.Graph.Entrypoint);
        var contracts = new JsonObject(workflow.Inputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, PlanningGraphCompiler.ToJsonSchema(p.Schema, state.Preparation!))));
        var fingerprint = PlanningGraphCompiler.Fingerprint(Context(state) + "\n" + contracts.ToJsonString());
        if (state.ScenarioInputs is not null && state.ScenarioInputsFingerprint == fingerprint) return true;
        state.BestScenarios.Clear();
        if (workflow.Inputs.Count == 0) { state.ScenarioInputs = new(); state.ScenarioInputsFingerprint = fingerprint; return true; }
        var defaults = workflow.Inputs.Where(p => p.Default is not null && PlanningGraphValidation.IsLiteral(p.Default)).ToArray();
        if (defaults.Length == workflow.Inputs.Count)
        {
            state.ScenarioInputs = new(defaults.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, PlanningGraphValidation.Literal(p.Default!))));
            state.ScenarioInputsFingerprint = fingerprint; return true;
        }
        var definitions = PlanningSchemas.Graph(state.Preparation!)["$defs"]!.DeepClone().AsObject();
        var variants = definitions["value"]!["anyOf"]!.AsArray();
        foreach (var variant in variants.ToArray())
        {
            var kinds = variant!["properties"]!["kind"]!["enum"]!.AsArray();
            foreach (var kind in kinds.ToArray()) if (kind?.ToString() is not ("string" or "number" or "boolean" or "null" or "object" or "array")) kinds.Remove(kind);
            if (kinds.Count == 0) variants.Remove(variant);
        }
        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject(workflow.Inputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, new JsonObject { ["$ref"] = "#/$defs/value" }))),
            ["required"] = new JsonArray(workflow.Inputs.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()), ["$defs"] = definitions };
        PlanningConstruction.PruneDefinitions(schema);
        var prompt = "Construct a nominal validation input fixture for the declared workflow. Use literal typed values only. " +
            "Respect the intended domain and each input contract; generic placeholders may not satisfy formats such as URLs or structured identifiers. " +
            "Use supplied example values when applicable. These values are for deterministic fake execution only and will never become workflow defaults or external call arguments. " +
            "No live external effects are performed in these scenarios.\nBehavior:\n" + Context(state) + "\nInput contracts:\n" + contracts.ToJsonString();
        var findings = new List<PlanningDiagnostic>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var actualPrompt = prompt + (findings.Count == 0 ? "" : "\nRepair these fixture fields:\n" + JsonSerializer.Serialize(findings, PlanningJsonContext.Default.ListPlanningDiagnostic));
            if (PlanningConstruction.EstimateInputTokens(actualPrompt, schema) > state.Request.Generation.MaxInputTokensPerUnit)
            { findings = [new("SCENARIO_INPUT_CONTEXT_TOO_LARGE", "/scenarioInputs", "The validation fixture exceeds the configured input context limit. No model request was sent.")]; break; }
            JsonObject candidate;
            try { candidate = await StructuredAsync(state, runtime, "scenario_inputs", actualPrompt, schema, ct, maxAttempts: 1); }
            catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException ex) when (ex.Code == GnOuGo.Flow.Core.Models.ErrorCodes.LlmSchema)
            { findings = [new("SCENARIO_INPUT_INVALID", "/scenarioInputs", "The model did not supply schema-valid literal fixture values.")]; continue; }
            var inputs = new JsonObject(); findings.Clear();
            foreach (var port in workflow.Inputs)
            {
                var value = JsonSerializer.Deserialize(candidate[port.Name]!, PlanningJsonContext.Default.PlanningValue)!;
                if (!PlanningGraphValidation.IsLiteral(value)) { findings.Add(new("SCENARIO_INPUT_INVALID", "/scenarioInputs/" + port.Name, "Validation fixtures must be literal values.")); continue; }
                inputs[port.Name] = PlanningGraphValidation.Literal(value);
                findings.AddRange(PlanningContractValidation.ValidateInstance(inputs[port.Name], contracts[port.Name]!.AsObject()).Select(error => new PlanningDiagnostic("SCENARIO_INPUT_INVALID", "/scenarioInputs/" + port.Name, error)));
            }
            if (findings.Count != 0) continue;
            state.ScenarioInputs = inputs; state.ScenarioInputsFingerprint = fingerprint;
            await runtime.CheckpointAsync(state, ct); return true;
        }
        state.Diagnostics = findings; state.CurrentPhase = "scenario_inputs"; state.Status = PlanningStatus.Recovery; return false;
    }
}
