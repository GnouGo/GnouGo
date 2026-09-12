using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed record PlanningValidationReport(int Stage, List<PlanningDiagnostic> Diagnostics, List<PlanningScenarioResult> Scenarios, string? Yaml = null);

/// <summary>Ordered gates. A repair is assessed through the baseline's first failing gate.</summary>
internal sealed class PlanningValidationPipeline
{
    private readonly PlanningGraphCompiler _compiler = new();
    private readonly PlanningScenarioFixtures _fixtures = new();
    private readonly PlanningSemanticReview _review = new();

    internal static List<PlanningDiagnostic> TypedFindings(PlanningSnapshot state, PlanningGraph graph)
    {
        var diagnostics = new List<PlanningDiagnostic>();
        if (state.Construction.Dataflow?.ContractFingerprint != PlanningContext.Contracts(state))
            diagnostics.Add(new("LOCKED_CONTRACT_CHANGED", "/preparation", "Locked contracts changed and require renewed human review."));
        diagnostics.AddRange(PlanningGraphValidation.Validate(graph, state.Preparation!));
        diagnostics.AddRange(PlanningExecutableValidation.Validate(graph, state.Preparation!));
        diagnostics.AddRange(PlanningArtifactBindings.PrerequisiteFindings(graph, state.Preparation!));
        diagnostics.AddRange(PlanningBehaviorPlans.ValidateImplementation(state.BehaviorPlan!, graph, state.Preparation!));
        diagnostics.AddRange(PlanningDataflowResolver.Validate(state, graph));
        if (state.ApprovedBehaviorHash != PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan!))
            diagnostics.Add(new("BEHAVIOR_APPROVAL_INVALID", "/behavior", "The accepted behavior changed and requires review."));
        return diagnostics.Where(d => !state.Construction.Workflows.Where(w => w.Status == "pending")
            .Any(w => PlanningDataflowResolver.Owns(d, graph.Workflows.FindIndex(p => p.Key == w.WorkflowKey), w.WorkflowKey))).Distinct().ToList();
    }

    internal async Task<PlanningValidationReport> EvaluateAsync(PlanningSnapshot state, PlanningGraph graph, IPlanningRuntime runtime, CancellationToken ct, int throughStage = 4)
    {
        var diagnostics = TypedFindings(state, graph);
        if (diagnostics.Any(d => d.Required)) return new(1, diagnostics, []);
        if (throughStage == 1 || state.Construction.Workflows.Any(w => w.Status == "pending")) return new(2, diagnostics, []);
        string yaml;
        try { yaml = _compiler.Compile(graph, state.Preparation!, state.Request.Name); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or GnOuGo.Flow.Core.Compilation.WorkflowCompilationException)
        { return new(2, [new("GRAPH_LOWERING_INVALID", "$", ex.Message)], []); }
        diagnostics.AddRange((await runtime.ValidateAsync(new(yaml, PlanningContext.EffectiveRequest(state), state.Preparation!, PlanningGraphCompiler.CapabilityBindings(graph)), ct))
            .Select(d => PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph)));
        if (diagnostics.Any(d => d.Required)) return new(2, diagnostics, [], yaml);
        if (throughStage == 2) return new(3, diagnostics, [], yaml);
        return await CompleteAsync(state, graph, runtime, ct, diagnostics, yaml, throughStage);
    }

    private async Task<PlanningValidationReport> CompleteAsync(PlanningSnapshot state, PlanningGraph graph, IPlanningRuntime runtime, CancellationToken ct,
        List<PlanningDiagnostic> diagnostics, string yaml, int throughStage)
    {
        if (state.Validation.Inputs is null) return new(3, [new("SCENARIO_INPUTS_REQUIRED", "/scenarioInputs", "Validation fixtures have not been established.")], [], yaml);
        var main = graph.Workflows.Single(w => w.Key == graph.Entrypoint);
        foreach (var port in main.Inputs)
            if (port.Required && !state.Validation.Inputs.ContainsKey(port.Name) || state.Validation.Inputs.ContainsKey(port.Name) &&
                PlanningContractValidation.ValidateInstance(state.Validation.Inputs[port.Name], PlanningGraphCompiler.ToJsonSchema(port.Schema, state.Preparation!)).Count != 0)
                diagnostics.Add(new("SCENARIO_FIXTURE_CONTRACT_CHANGED", "/scenarioInputs/" + port.Name, "Preserve established validation fixtures and their proven contracts."));
        if (diagnostics.Any(d => d.Required)) return new(3, diagnostics, [], yaml);
        var scenarios = (await runtime.ValidateScenariosAsync(new(yaml, state.Preparation!, state.Validation.Inputs,
            PlanningScenarioFixtures.ScenarioLoopItemSchemas(graph, state.Preparation!), state.Validation.Observations), ct))
            .Select(s => s with { Diagnostics = s.Diagnostics.Select(d => PlanningExecutableValidation.MapRuntimeDiagnostic(d, graph)).ToList() }).ToList();
        if (scenarios.Count == 0) diagnostics.Add(new("SCENARIO_MISSING", "$", "No scenario coverage was established."));
        foreach (var scenario in scenarios.Where(s => s.Outcome != "passed"))
            diagnostics.AddRange(scenario.Diagnostics.Count == 0 ? [new("SCENARIO_INCONCLUSIVE", scenario.Id, "Required scenario coverage is incomplete.")] : scenario.Diagnostics);
        if (diagnostics.Any(d => d.Required)) return new(3, diagnostics.Distinct().ToList(), scenarios, yaml);
        if (throughStage == 3) return new(4, diagnostics, scenarios, yaml);
        diagnostics.AddRange(await _review.ReviewAsync(state, graph, runtime, ct));
        return new(diagnostics.Any(d => d.Required) ? 4 : 5, diagnostics, scenarios, yaml);
    }

    internal async Task AdvanceAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        PlanningDataflowResolver.Refresh(state);
        var report = await EvaluateAsync(state, state.Graph!, runtime, ct, throughStage: 2);
        if (!report.Diagnostics.Any(d => d.Required))
        {
            foreach (var workflow in state.Construction.Workflows.Where(w => w.Status == "constructed")) workflow.Status = "validated";
            if (state.Construction.Workflows.Any(w => w.Status == "pending"))
            { state.Diagnostics.Clear(); state.Status = PlanningStatus.Generating; return; }
            if (!await _fixtures.PrepareInputsAsync(state, runtime, ct) || !await _fixtures.PrepareObservationsAsync(state, runtime, ct)) return;
            state.Validation.FixturesEstablished = true;
            report = await CompleteAsync(state, state.Graph!, runtime, ct, report.Diagnostics, report.Yaml!, 4);
        }
        state.Validation.ContractFingerprint = PlanningContext.Contracts(state);
        state.Validation.FixtureFingerprint = PlanningContext.Fixtures(state);
        state.Validation.Stage = report.Stage;
        foreach (var workflow in state.Construction.Workflows.Where(w => w.Status != "pending")) workflow.Gate = PlanningGates.FromStage(report.Stage);
        state.Validation.GraphFingerprint = PlanningGraphCompiler.Fingerprint(state.Graph!);
        state.Validation.Scenarios = report.Scenarios;
        state.Diagnostics = report.Diagnostics;
        state.Attempts.Add(new(state.Validation.GraphFingerprint, "validation", report.Stage, true, report.Diagnostics));
        foreach (var workflow in state.Graph!.Workflows)
        {
            var index = state.Graph.Workflows.IndexOf(workflow);
            PlanningConvergence.Failure(state, workflow.Key, PlanningGates.FromStage(report.Stage), state.Validation.GraphFingerprint,
                report.Diagnostics.Where(d => PlanningDataflowResolver.Owns(d, index, workflow.Key)));
        }
        PlanningConvergence.Failure(state, "$plan", PlanningGates.FromStage(report.Stage), state.Validation.GraphFingerprint,
            report.Diagnostics.Where(d => !state.Graph.Workflows.Any(w => PlanningDataflowResolver.Owns(d, state.Graph.Workflows.IndexOf(w), w.Key))));
        if (report.Diagnostics.Any(d => d.Required))
        {
            PlanningContext.InvalidateArtifact(state);
            if (report.Diagnostics.Any(d => d.Location.EndsWith("/behavior", StringComparison.Ordinal) || d.Location.EndsWith("/preparation", StringComparison.Ordinal)))
                PlanningContext.Stop(state, "GOVERNING_CONTRACT_REVIEW_REQUIRED", "Revise the governing contract and review the behavior before continuing.");
            else { state.Status = PlanningStatus.Generating; state.CurrentPhase = PlanningPhase.Repair; }
            return;
        }
        state.Yaml = report.Yaml!;
        state.ArtifactHash = PlanningGraphCompiler.Fingerprint(state.Yaml);
        state.ApprovedHash = null; state.Status = PlanningStatus.FinalReview;
        state.ReviewMarkdown = state.BehaviorPlan!.Summary;
    }
}
