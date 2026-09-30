using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedOperationOutputTests
{
    [Fact]
    public async Task OriginalResponsesReachReviewWithTheSamePlanReceiptsAndAccounting()
    {
        var replay = new Replay(); var planner = new HybridWorkflowPlanner(); PlanningSession? result = null;
        foreach (var entry in replay.Recording["responses"]!.AsArray())
        {
            replay.Expected = entry!.AsObject(); var pending = replay.State(entry["pendingSession"]!);
            var before = JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession);
            result = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, replay, TestContext.Current.CancellationToken);
            Assert.Equal(before, JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(pending.ModelCalls, result.ModelCalls); Assert.Equal(pending.ReplanAttempts, result.ReplanAttempts);
            Assert.Equal(JsonSerializer.Serialize(pending.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), JsonSerializer.Serialize(result.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
            Assert.Null(result.PendingCall); Assert.Null(result.ApprovedHash);
        }
        Assert.Equal(4, replay.Identities.Count); Assert.Equal(4, result!.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
        Assert.True(result.Status == PlanningStatus.FinalReview, string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Empty(result.Diagnostics);
        var retained = replay.State(replay.Recording["finalSession"]!);
        Assert.Equal(JsonSerializer.Serialize(retained.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(result.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(JsonSerializer.Serialize(retained.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage), JsonSerializer.Serialize(result.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
        Assert.Equal(JsonSerializer.Serialize(retained.Discovery.Resolved, PlanningJsonContext.Default.ListPlanningCapability), JsonSerializer.Serialize(result.Discovery.Resolved, PlanningJsonContext.Default.ListPlanningCapability));
        Assert.Equal(JsonSerializer.Serialize(retained.Discovery.Inspections, PlanningJsonContext.Default.ListPlanningDiscoveryRequest), JsonSerializer.Serialize(result.Discovery.Inspections, PlanningJsonContext.Default.ListPlanningDiscoveryRequest));
        Assert.Contains(result.Discovery.Limitations, l => l.StartsWith("Discovery is incomplete:", StringComparison.Ordinal));
        Assert.Single(retained.Diagnostics); Assert.All(retained.Diagnostics, d => Assert.Equal("TASK_COMPILER_VALIDATION", d.Code));
        PlanningArtifactApproval.Verify(result);

        // Recovery uses the original request/schema/patch authority, with no new request or metadata.
        var recoveredRuntime = new Replay(); recoveredRuntime.Expected = recoveredRuntime.Recording["responses"]!.AsArray().Last()!.AsObject();
        var recovered = await planner.AdvanceAsync(recoveredRuntime.State(recoveredRuntime.Expected["pendingSession"]!),
            new() { ExpectedRevision = recoveredRuntime.Expected["pendingSession"]!["revision"]!.GetValue<long>() }, recoveredRuntime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Single(recoveredRuntime.Identities); Assert.Equal(0, recoveredRuntime.MetadataReads);
        Assert.Equal(result.Yaml, recovered.Yaml); Assert.Equal(result.ComputeArtifactHash(), recovered.ComputeArtifactHash());
        PlanningArtifactApproval.Verify(recovered);
        var hash = recovered.ComputeArtifactHash(); recovered.Plan!.Root.Always[0].Objective += " changed";
        Assert.NotEqual(hash, recovered.ComputeArtifactHash()); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(recovered));
        var stale = recoveredRuntime.State(recoveredRuntime.Expected["pendingSession"]!);
        stale.Plan!.Root.Tasks[0].Objective += " unauthorized revision";
        var staleBefore = JsonSerializer.Serialize(stale, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(stale, new() { ExpectedRevision = stale.Revision }, recoveredRuntime, TestContext.Current.CancellationToken));
        Assert.Single(recoveredRuntime.Identities); Assert.Equal(staleBefore, JsonSerializer.Serialize(stale, PlanningJsonContext.Default.PlanningSession));
    }

    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OperationOutputs", "retained-output-contracts.json")))!.AsObject();
        internal JsonObject Expected = null!;
        internal readonly List<string> Identities = [];
        internal int MetadataReads;
        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;
        internal PlanningSession State(JsonNode compact)
        {
            var json = compact.DeepClone().AsObject();
            var pages = json["recordedPages"]!.GetValue<int>();
            var resolved = json["recordedResolved"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var ids = json["recordedCatalogIds"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var query = json["presentationQuery"]?.GetValue<string>(); var inspections = json["recordedInspections"]?.DeepClone();
            foreach (var key in new[] { "recordedPages", "recordedResolved", "recordedCatalogIds", "presentationQuery", "recordedInspections" }) json.Remove(key);
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            state.Catalog = Recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!; state.Catalog.Capabilities.RemoveAll(c => !ids.Contains(c.Id));
            state.Discovery = Discovery; state.Discovery.Pages = state.Discovery.Pages.Take(pages).ToList(); state.Discovery.Resolved.RemoveAll(c => !resolved.Contains(c.Id));
            state.Discovery.PresentationQuery = query; state.Discovery.Inspections = inspections?.Deserialize(PlanningJsonContext.Default.ListPlanningDiscoveryRequest);
            return state;
        }
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("Catalog already retained");
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("Sources already retained");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { MetadataReads++; return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query && p.ProducedArtifactKind == producedArtifactKind)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
            => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            var issued = Expected["pendingSession"]!["pendingCall"]!;
            Assert.Equal(issued["id"]!.ToString(), request.ClientRequestId); Assert.Equal(issued["request"]!["prompt"]!.ToString(), request.Prompt);
            Assert.True(JsonNode.DeepEquals(issued["request"]!["structuredOutputSchema"], request.StructuredOutputSchema));
            Assert.DoesNotContain(request.ClientRequestId!, Identities); Identities.Add(request.ClientRequestId!);
            return Task.FromResult(Expected["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval or execution");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
