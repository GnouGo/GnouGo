using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BatchedDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TwoRelevantSourcesNeedOnlyDiscoveryAndTaskPlanAndReuseReceipts()
    {
        var fixture = new ProductTransformationFixture();
        var runtime = new TestRuntime(new() { McpClientFactory = fixture.Factory(distractors: true) });
        var tracking = new Tracking(runtime.Capabilities); runtime.Capabilities = tracking;
        var sources = await tracking.ListSourcesAsync(PlannerFixture.Ct);
        var browser = sources.Single(s => s.Description.Contains("Browse web pages", StringComparison.Ordinal));
        var document = sources.Single(s => s.Description.Contains("Write documents", StringComparison.Ordinal));
        runtime.Respond = (request, _) =>
        {
            var proposal = new PlanningProposal { Requirements = PlannerFixture.Requirements() };
            if (runtime.Calls.Count == 1) proposal.DiscoveryRequests = [new(browser.Id), new(document.Id)];
            else
            {
                var saved = runtime.Checkpoints[^1];
                var issued = new PlanningCatalog { Capabilities = saved.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => new PlanningCapability
                    { Id = c.Id, Operation = c.Operation, Method = c.Name, Server = c.SourceId == browser.Id ? fixture.BrowserSource : fixture.DocumentSource }).ToList() };
                proposal.Plan = ProductTransformationPlan.Create(issued, fixture);
            }
            return TestRuntime.Response(request, proposal);
        };
        var session = PlannerFixture.Session(); session.Request.Generation.MaxInputTokensPerRequest = 24000;
        var state = await PlannerFixture.RunAsync(runtime, session);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, tracking.Pages.Count); Assert.Equal(3, tracking.Resolutions); Assert.Equal(13, state.Discovery.Pages.Sum(p => p.Capabilities.Count));
        Assert.Equal(2, runtime.Calls.Count); Assert.Empty(fixture.Calls); Assert.Empty(fixture.Effects);
        foreach (var request in runtime.Calls)
        {
            var estimate = PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject());
            Assert.InRange(estimate, 1, 24000);
            output.WriteLine($"request {request.ClientRequestId}: prompt chars={request.Prompt.Length}, schema chars={request.StructuredOutputSchema.ToJsonString().Length}, conservative input tokens={estimate}");
        }
        var recovered = PlannerFixture.Clone(state);
        Assert.Equal(state.ComputeArtifactHash(), recovered.ComputeArtifactHash());
        var receipts = JsonSerializer.Serialize(recovered.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        recovered = await new HybridWorkflowPlanner().AdvanceAsync(recovered, new() { Kind = "approve", ExpectedRevision = recovered.Revision, ArtifactHash = recovered.ComputeArtifactHash() }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Approved, recovered.Status); Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(receipts, JsonSerializer.Serialize(recovered.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        var hash = recovered.ComputeArtifactHash(); recovered.Plan!.Root.Tasks[1].ResultType!.Fields[0].Type.Items!.Nullable = true;
        Assert.NotEqual(hash, recovered.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(recovered));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("cursor")]
    [InlineData("conflict")]
    [InlineData("oversized")]
    public async Task InvalidBatchNeverFetchesPartialPages(string defect)
    {
        var runtime = new TestRuntime(); var catalog = new TwoSources(); runtime.Capabilities = catalog;
        runtime.Proposal.DiscoveryRequests = defect == "oversized" ? [new("a"), new("b"), new("a"), new("b"), new("a")] : [new("a"), new(defect == "duplicate" ? "a" : "b", defect == "cursor" ? "invented" : null)];
        if (defect != "conflict") runtime.Proposal.Plan = null;
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        Assert.Empty(catalog.Pages); Assert.NotEmpty(state.Diagnostics); Assert.Equal(1, state.ModelCalls);
    }

    [Fact]
    public async Task InterruptedBatchReusesPendingIdentityAndRecordedResponse()
    {
        var runtime = new TestRuntime(); var catalog = new TwoSources { Interrupt = true }; runtime.Capabilities = catalog;
        runtime.Proposal.Plan = null; runtime.Proposal.DiscoveryRequests = [new("a"), new("b")];
        var recorded = new Dictionary<string, LLMResponse>(StringComparer.Ordinal);
        runtime.Respond = (request, _) => recorded.TryGetValue(request.ClientRequestId!, out var receipt) ? receipt : recorded[request.ClientRequestId!] = TestRuntime.Response(request, runtime.Proposal);
        var planner = new HybridWorkflowPlanner();
        await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        var recovered = PlannerFixture.Clone(runtime.Checkpoints.First(s => s.PendingCall is not null));
        var identity = recovered.PendingCall!.Id; catalog.Interrupt = false;
        recovered = await planner.AdvanceAsync(recovered, new() { ExpectedRevision = recovered.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(1, recovered.ModelCalls); Assert.Equal(0, recovered.ReplanAttempts); Assert.Single(recorded);
        Assert.All(runtime.Calls, r => Assert.Equal(identity, r.ClientRequestId)); Assert.Equal(2, recovered.Discovery.Pages.Count);
        Assert.Equal(new[] { "a", "b", "a", "b" }, catalog.Pages);
    }

    [Fact]
    public async Task SupersededPendingContractFailsWithoutRedispatchOrAccountingMutation()
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new IOException("Interrupted") };
        var state = await PlannerFixture.RunAsync(runtime);
        var pending = state.PendingCall!; pending.Request.StructuredOutputSchema!["properties"]!["sourceId"] = new JsonObject { ["type"] = "null" };
        var retained = JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningModelCall);
        state.Status = PlanningStatus.Generating;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(retained, JsonSerializer.Serialize(state.PendingCall, PlanningJsonContext.Default.PlanningModelCall));
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_REQUEST_INCOMPATIBLE" && d.Message.Contains("new planning session", StringComparison.Ordinal));
    }

    private sealed class Tracking(ICapabilityCatalog inner) : ICapabilityCatalog
    {
        internal readonly List<string> Pages = []; internal int Resolutions;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => inner.ListSourcesAsync(ct);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct) { Pages.Add(sourceId); return inner.ListAsync(sourceId, cursor, ct); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) { Resolutions++; return inner.ResolveAsync(summary, ct); }
    }
    private sealed class TwoSources : ICapabilityCatalog
    {
        internal readonly List<string> Pages = []; internal bool Interrupt;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("a", "First"), new("b", "Second")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct)
        { Pages.Add(sourceId); if (Interrupt && sourceId == "b") throw new OperationCanceledException("Interrupted metadata read"); return Task.FromResult(new CapabilityPage(sourceId, cursor, [], null)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
    }
}
