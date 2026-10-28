using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

internal static class CompactConstructionSmoke
{
    internal static async Task RunAsync()
    {
        var fresh = await CurrentPlanningFixture.LiteralAsync("smoke", "current-literal", null, CancellationToken.None);
        if (fresh.SchemaVersion != 11 || fresh.ModelCalls != 5 || fresh.Construction is not { Complete: true, Candidate: null } ||
            fresh.ApprovedHash is not null || fresh.RequestMeasurements?.Any(measurement =>
                measurement.Components.Sum(component => component.SerializedBytes) != measurement.CompleteRequestBytes) != false)
            throw new InvalidOperationException("Current construction or reconciled measurements failed under Native AOT.");
        var pending = await CurrentPlanningFixture.PendingLiteralReviewAsync("smoke", "current-recovery", CancellationToken.None);
        var exact = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = pending.ModelCalls;
        pending = JsonSerializer.Deserialize(JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        if (JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest) != exact)
            throw new InvalidOperationException("The exact issued current review changed during source-generated recovery.");
        var receipt = new SavedReview(exact);
        IPlanningRuntime runtime = receipt;
        var planner = new HybridWorkflowPlanner();
        var reviewed = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, CancellationToken.None);
        if (reviewed.Status != PlanningStatus.FinalReview || reviewed.PendingCall is not null || reviewed.ModelCalls != calls || receipt.Calls != 1)
            throw new InvalidOperationException("Saved current review did not recover without another reservation: " + reviewed.Status +
                "; calls=" + receipt.Calls + "; " + string.Join("; ", reviewed.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        PlanningArtifactApproval.Verify(reviewed);
        reviewed = await planner.AdvanceAsync(reviewed, new() { Kind = "approve", ExpectedRevision = reviewed.Revision,
            ArtifactHash = reviewed.ComputeArtifactHash(), ReviewedRequirementIds = reviewed.Requirements!.Outcomes.Select(requirement => requirement.Id).ToList() }, runtime, CancellationToken.None);
        if (reviewed.Status != PlanningStatus.Approved || receipt.Calls != 1)
            throw new InvalidOperationException("Explicit artifact approval bypassed or repeated the current business review.");
        var engine = new WorkflowEngine();
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(reviewed.Yaml!));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), CancellationToken.None);
        if (!result.Success || result.Outputs?["result"]?.GetValue<double>() != 42)
            throw new InvalidOperationException("The current TaskPlan did not execute its independently checked numeric result.");
        var archive = JsonSerializer.SerializeToNode(reviewed, PlanningJsonContext.Default.PlanningSession)!;
        archive["schemaVersion"] = 10;
        try { PlanningSessionStorage.Read(archive.ToJsonString(), "smoke", "current-recovery"); }
        catch (PlanningConflictException) { Console.WriteLine("current construction: five calls, exact global receipt recovery, explicit approval, format-11 rejection and executed result passed"); return; }
        throw new InvalidOperationException("The obsolete planning format was accepted.");
    }

    private sealed class SavedReview(string exact) : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _validation = new(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        public ICapabilityCatalog Capabilities => _validation.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("Receipt recovery cannot discover capabilities.");
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _validation.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _validation.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (++Calls != 1 || JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest) != exact ||
                request.StructuredOutputSchema?["title"]?.ToString() != "construction_global_judge")
                throw new InvalidOperationException("Only the exact saved global receipt may be consumed.");
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["decision"] = new JsonObject
                { ["verdict"] = "ok", ["reason"] = "The current artifact returns the original required number without additional effects." } } });
        }
    }
}
