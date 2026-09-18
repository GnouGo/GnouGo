using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class PlanningValidationPipeline
{
    internal static async Task ValidateAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var graph = state.Graph!; var catalog = state.Catalog!;
        PlanningConfirmationGuards.Apply(graph, catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(graph, catalog).ToList();
        if (state.Diagnostics.Any(d => d.Required)) return;
        string yaml;
        try { yaml = new PlanningGraphCompiler().Compile(graph, catalog, state.Request.Name); }
        catch (WorkflowCompilationException ex) { state.Diagnostics.AddRange(PlanningExecutableValidation.CompilerErrors(ex, graph)); return; }
        state.Diagnostics.AddRange((await runtime.ValidateAsync(new(yaml, state.Request, catalog, PlanningGraphCompiler.CapabilityBindings(graph)), ct)).Select(d => PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph)));
        if (state.Diagnostics.Any(d => d.Required)) return;
        var fixtures = state.IntentPlan!.Fixtures;
        JsonObject? inputs = null;
        if (fixtures?.Inputs is { } sample)
        {
            if (!PlanningGraphValidation.IsLiteral(sample) || PlanningGraphValidation.Literal(sample) is not JsonObject obj)
            { state.Diagnostics.Add(new("SCENARIO_INPUT_INVALID", "/fixtures/inputs", "Scenario inputs must be a literal object.")); return; }
            inputs = obj;
        }
        var observations = new JsonObject();
        foreach (var observation in fixtures?.Observations ?? [])
        {
            var owner = observation.Workflow == graph.Entrypoint && graph.Workflows.Any(w => w.Key == PlanningConfirmationGuards.Body) ? PlanningConfirmationGuards.Body : observation.Workflow;
            var workflow = graph.Workflows.FirstOrDefault(w => w.Key == owner);
            var node = workflow is null ? null : PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == observation.Node);
            if (node is null || node.Type is not ("mcp.call" or "llm.call") || observation.Responses.Any(v => !PlanningGraphValidation.IsLiteral(v)))
            { state.Diagnostics.Add(new("SCENARIO_OBSERVATION_INVALID", "/fixtures/observations", "Observations require an existing external step and literal results.")); continue; }
            var schema = node.StructuredOutput is not null ? PlanningGraphCompiler.ToJsonSchema(node.StructuredOutput.Schema, catalog)
                : catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId)?.OutputSchema;
            if (schema is null || schema.Count == 0)
            { state.Diagnostics.Add(new("SCENARIO_OBSERVATION_SCHEMA", "/fixtures/observations", "The observed result needs a declared schema.")); continue; }
            var key = (workflow!.Key == graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..16]) + ":n_" + PlanningGraphCompiler.Fingerprint(node.Key)[..16];
            observations[key] = new JsonObject { ["schema"] = schema.DeepClone(), ["channel"] = node.StructuredOutput is null ? "response" : "json", ["responses"] = new JsonArray(observation.Responses.Select(PlanningGraphValidation.Literal).ToArray()) };
        }
        if (state.Diagnostics.Any(d => d.Required)) return;
        var loopItems = new JsonObject();
        foreach (var workflow in graph.Workflows)
            foreach (var loop in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => n.Type is "loop.sequential" or "loop.parallel"))
                if (PlanningGraphValidation.Member(loop.Input, "items") is { } items && PlanningGraphValidation.ResolveValueContract(graph, workflow, items, catalog)["items"] is JsonObject schema)
                    loopItems[(workflow.Key == graph.Entrypoint ? "main" : "w_" + PlanningGraphCompiler.Fingerprint(workflow.Key)[..16]) + ":n_" + PlanningGraphCompiler.Fingerprint(loop.Key)[..16]] = schema.DeepClone();
        state.Scenarios = (await runtime.ValidateScenariosAsync(new(yaml, catalog, inputs, loopItems, observations), ct)).ToList();
        if (state.Scenarios.Count == 0) state.Diagnostics.Add(new("SCENARIO_MISSING", "$", "No scenario execution was reported."));
        foreach (var scenario in state.Scenarios.Where(s => s.Outcome != "passed"))
            state.Diagnostics.AddRange((scenario.Diagnostics.Count > 0 ? scenario.Diagnostics : [new("SCENARIO_INCONCLUSIVE", scenario.Id, scenario.Description)]).Select(d => PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph)));
        if (state.Diagnostics.Any(d => d.Required)) return;
        state.Yaml = yaml; state.ApprovedHash = null; state.Status = PlanningStatus.FinalReview;
    }
}
