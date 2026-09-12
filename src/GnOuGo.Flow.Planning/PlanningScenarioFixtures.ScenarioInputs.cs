using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed partial class PlanningScenarioFixtures
{
    internal static JsonObject ScenarioLoopItemSchemas(PlanningGraph graph, PlanningPreparation preparation)
    {
        var result = new JsonObject();
        foreach (var workflow in graph.Workflows)
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => n.Type == "loop.sequential"))
            {
                var items = node.Input.Members.FirstOrDefault(m => m.Name is "items" or "over")?.Value;
                if (items is null) continue;
                var contract = PlanningGraphValidation.ResolveValueContract(graph, workflow, items, preparation);
                if (contract["items"] is not JsonObject item || item.Count == 0) continue;
                var key = (workflow.Key == graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..16]) + ":n_" + PlanningGraphCompiler.Fingerprint(node.Key)[..16];
                result[key] = item.DeepClone();
            }
        return result;
    }

    // Validation fixtures are private planning evidence. They never become runtime
    // defaults or literal arguments and cannot alter the approved executable behavior.
    internal async Task<bool> PrepareInputsAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var workflow = state.Graph!.Workflows.Single(w => w.Key == state.Graph.Entrypoint);
        var contracts = new JsonObject(workflow.Inputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, PlanningGraphCompiler.ToJsonSchema(p.Schema, state.Preparation!))));
        var fingerprint = PlanningGraphCompiler.Fingerprint(PlanningContext.Intent(state) + "\n" + contracts.ToJsonString());
        bool ValidFixture(JsonObject inputs) => inputs.Count == workflow.Inputs.Count && workflow.Inputs.All(p => inputs.ContainsKey(p.Name) &&
            (!p.Required || inputs[p.Name] is not null) && PlanningContractValidation.ValidateInstance(inputs[p.Name], contracts[p.Name]!.AsObject()).Count == 0);
        if (state.Validation.Inputs is not null) return true;

        if (workflow.Inputs.Count == 0) { state.Validation.Inputs = new(); state.Validation.InputsFingerprint = fingerprint; return true; }
        var defaults = workflow.Inputs.Where(p => p.Default is not null && PlanningGraphValidation.IsLiteral(p.Default)).ToArray();
        if (defaults.Length == workflow.Inputs.Count)
        {
            var values = new JsonObject(defaults.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, PlanningGraphValidation.Literal(p.Default!))));
            if (ValidFixture(values)) { state.Validation.Inputs = values; state.Validation.InputsFingerprint = fingerprint; return true; }
        }
        state.CurrentPhase = "scenario_inputs";
        var inputs = new JsonObject();
        foreach (var port in workflow.Inputs)
        {
            inputs[port.Name] = await FixtureAsync(state, runtime, "scenario_inputs", workflow.Key, "/scenarioInputs/" + PlanningFieldPaths.Escape(port.Name),
                contracts[port.Name]!.AsObject(), new JsonObject { ["businessInput"] = state.BehaviorPlan?.Workflows.Single(w => w.Key == workflow.Key).Inputs.SingleOrDefault(p => p.Name == port.Name)?.Description ?? port.Schema.Description,
                    ["task"] = "Supply a nominal synthetic input value for validation. It is private fixture data, never an executable default or evidence of a real external observation." }, ct);
        }
        if (!ValidFixture(inputs))
        {
            state.Diagnostics = [new("SCENARIO_INPUT_INVALID", "/scenarioInputs", "The assembled fixture does not satisfy the executable input contracts.")];
            state.Status = PlanningStatus.Stopped; return false;
        }
        state.Validation.Inputs = inputs; state.Validation.InputsFingerprint = fingerprint;
        await runtime.CheckpointAsync(state, ct); return true;
    }
}
