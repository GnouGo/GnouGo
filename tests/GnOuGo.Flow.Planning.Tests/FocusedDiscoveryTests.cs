using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FocusedDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourPagesThenRecreatedRuntimeUsesPinnedSnapshotDespiteChangedRemoteCatalog(bool diagnostic)
    {
        var source = new Source("arbitrary", 48);
        var (state, runtime) = Ready(source);
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(4, state.Discovery.Pages.Count);
        Assert.NotNull(state.Construction!.Compact!.CurrentStep!.PendingDiscovery!.InlineSnapshots);
        Assert.Equal(1, source.Reads); Assert.Equal(1, state.ModelCalls);
        var version = Assert.Single(state.Discovery.Pages.Select(p => p.SnapshotVersion).Distinct());
        source.Tools[0].Description = "A changed remote contract after the fourth page";
        var resumedRuntime = new TestRuntime { Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source }),
            Respond = (_, _) => throw new InvalidOperationException("Pagination must not infer again.") };
        state = await Advance(PlannerFixture.Clone(state), resumedRuntime);
        Assert.Empty(state.Diagnostics); Assert.Equal(6, state.Discovery.Pages.Count);
        Assert.Null(state.Construction!.Compact!.CurrentStep!.PendingDiscovery); Assert.Empty(resumedRuntime.Calls);
        Assert.Equal(1, source.Reads); Assert.All(state.Discovery.Pages, p => Assert.Equal(version, p.SnapshotVersion));
        Assert.Equal(48, state.Discovery.Pages.SelectMany(p => p.Capabilities).Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("unrelated", "document")]
    [InlineData("alpha", "beta")]
    public async Task FailedExploratorySourceWarnsAndHealthyAlternativeRemainsAvailable(string failedName, string healthyName)
    {
        var source = new Source(healthyName, 2);
        var (state, runtime) = Ready(source);
        var failedId = CapabilityDiscovery.SourceId(failedName);
        state.Discovery.Sources.Insert(0, new(failedId, "Exploratory metadata source"));
        runtime.Capabilities = new Mixed(source, failedId);
        runtime.Respond = (request, _) => Discover(request, state, failedId, source.Id);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.False(PlanningStatus.IsTerminal(state.Status));
        Assert.NotNull(state.Discovery.Failures); Assert.Single(state.Discovery.Failures);
        Assert.Contains(state.InspectionEvents!, e => e.State == "warning" && e.Discovery?.SourceId == failedId);
        Assert.Contains(state.Discovery.Pages, p => p.SourceId == source.Id && p.Capabilities.Count == 2);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(state.InspectionEvents, PlanningJsonContext.Default.ListPlanningInspectionEvent));
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Contains("transport_unavailable", context.ToJsonString()); Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
        var failures = state.Discovery.Failures.Count;
        await runtime.Capabilities.ListAsync(source.Id, null, Ct); // independent healthy discovery grants no binding authority
        Assert.Equal(failures, state.Discovery.Failures.Count); Assert.Empty(state.Catalog!.Capabilities);
    }

    [Fact]
    public async Task SelectedPrerequisiteIsTheExactQueryAndNoOtherSourceIsContacted()
    {
        var source = new Source("renamed", 1); var (state, runtime) = Ready(source);
        state.Construction!.Compact!.CurrentStep!.Prerequisites = [new() { Need = "Present the already prepared result to the operator", EvidenceAddresses = ["requirement:req_0"] }];
        var focus = PlanningDiscoveryFocus.Choices(state).Single(f => f.Basis == "retained_prerequisite");
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        runtime.Respond = (request, _) => Discover(request, state, [source.Id], focus.Id);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(focus.Query, Assert.Single(state.Discovery.Pages).Query);
        Assert.Equal(focus.Id, state.Discovery.Pages[0].FocusId); Assert.Equal(1, source.Reads);
        var invalid = JsonNode.Parse("{\"sourceId\":\"" + source.Id + "\",\"cursor\":null,\"query\":null,\"operationIds\":null,\"focusId\":\"another-step\",\"justification\":\"invented\"}")!;
        var response = new JsonObject { ["binding"] = null, ["discoveryRequests"] = new JsonArray(invalid), ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null };
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, schema));
    }

    [Fact]
    public async Task HistoricalFailureRecoveryPreservesWorkAndAccountingAndRequiresSeparateResume()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Construction!.Compact!.CurrentStep!.DiscoveryProtocol = null;
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical failure")];
        state.ModelCalls = 70; state.Usage = new() { Calls = 70, InputTokens = 100, OutputTokens = 50 };
        state.Discovery.Pages = [new(source.Id, "old-cursor", [], null, "Cause discarded", state.Construction.Compact.CurrentStep.Objective)];
        state.Construction.Compact.CurrentStep.PendingDiscovery = new() { Requests = [new(source.Id)], Pending = [new(source.Id, "old-cursor")] };
        var request = JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest);
        var plan = PlanningConstruction.Hash(state.Plan); var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "recover_discovery", ExpectedRevision = state.Revision,
            DiscoveryRecovery = new(failure.Id, "continue_available") }, runtime, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.True(PlanningStatus.IsWaiting(state.Status));
        Assert.Empty(runtime.Calls); Assert.Equal(0, source.Reads); Assert.Equal(70, state.ModelCalls);
        Assert.Equal(request, JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest));
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan)); Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Single(state.Discovery.Pages); Assert.Contains(state.Discovery.Failures!, f => f.Category == "historical_unavailable");
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { Kind = "resume_discovery", ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task RefreshStartsAtFirstPageAndKeepsOldFailureWithoutInference()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical")];
        state.Discovery.Pages = [new(source.Id, "stale", [], null, "Historical", state.Construction!.Compact!.CurrentStep!.Objective)];
        var id = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state)).Id;
        runtime.DiscoveryRecoveryGuard = (_, _) => Task.CompletedTask;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "recover_discovery", ExpectedRevision = state.Revision,
            DiscoveryRecovery = new(id, "refresh_source") }, runtime, Ct);
        Assert.Equal(PlanningStatus.DiscoveryRecovery, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(1, source.Reads);
        Assert.Equal(2, state.Discovery.Pages.Count); Assert.Null(state.Discovery.Pages[1].Cursor);
        Assert.Contains(id, state.Discovery.SupersededFailures!); Assert.Equal("stale", state.Discovery.Pages[0].Cursor);
    }

    [Theory]
    [InlineData("unavailable", false)]
    [InlineData("timeout", false)]
    [InlineData("changed", false)]
    [InlineData("invalid", false)]
    [InlineData("cycle", true)]
    [InlineData("ownership", true)]
    public async Task SourceFailuresRemainDistinctFromHardProtocolFailures(string outcome, bool hard)
    {
        var source = new Source("arbitrary", 0); var (state, runtime) = Ready(source);
        runtime.Capabilities = new Faulty(source.Id, outcome);
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        state = await Advance(state, runtime);
        Assert.Single(runtime.Calls); Assert.Empty(state.Catalog!.Capabilities); Assert.Null(state.ApprovedHash);
        if (hard)
        {
            Assert.True(PlanningStatus.IsTerminal(state.Status));
            Assert.True(state.Discovery.Failures is null or { Count: 0 });
        }
        else
        {
            Assert.False(PlanningStatus.IsTerminal(state.Status)); Assert.Empty(state.Diagnostics);
            Assert.Single(state.Discovery.Failures!); Assert.Null(state.Construction!.Compact!.CurrentStep!.Kind);
            Assert.Contains(state.InspectionEvents!, e => e.State == "warning");
            // All sources unavailable is unresolved context, never permission to invent an operation.
            Assert.Contains("canRequestPage\":false", PlanningCompactConstruction.DiscoveryContext(state).ToJsonString());
        }
    }

    [Fact]
    public async Task CancellationNeverBecomesAnUnavailableWarning()
    {
        var source = new Source("arbitrary", 0); var (state, runtime) = Ready(source);
        using var cancellation = new CancellationTokenSource();
        runtime.Capabilities = new Cancelling(cancellation);
        runtime.Respond = (request, _) => Discover(request, state, source.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, cancellation.Token));
        Assert.DoesNotContain(runtime.Checkpoints.SelectMany(s => s.InspectionEvents ?? []), e => e.State == "warning");
    }

    [Fact]
    public async Task RuntimeWithoutRecoveryAuthorityCannotChangeAStoppedSession()
    {
        var source = new Source("arbitrary", 1); var (state, runtime) = Ready(source);
        state.Status = PlanningStatus.Stopped; state.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "Historical")];
        state.Discovery.Pages = [new(source.Id, null, [], null, "Unavailable", state.Construction!.Compact!.CurrentStep!.Objective)];
        var failure = Assert.Single(PlanningDiscoveryRecoveryActions.Failures(state));
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "recover_discovery", ExpectedRevision = state.Revision, DiscoveryRecovery = new(failure.Id, "refresh_source") }, runtime, Ct));
        Assert.Empty(runtime.Calls); Assert.Equal(0, source.Reads); Assert.Single(state.Discovery.Pages);
    }

    internal static (PlanningSession State, TestRuntime Runtime) Ready(Source source)
    {
        var (state, runtime) = CompactDeferredBindingTests.Ready();
        state.Construction!.Compact!.CurrentStep!.DiscoveryProtocol = 2;
        state.Discovery.Sources = [new(source.Id, "Metadata for the requested business capability")];
        runtime.Capabilities = new CapabilityDiscovery(new() { McpClientFactory = source });
        return (state, runtime);
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
    private static LLMResponse Discover(LLMRequest request, PlanningSession state, params string[] sources) => Discover(request, state, sources, PlanningDiscoveryFocus.Choices(state)[0].Id);
    private static LLMResponse Discover(LLMRequest request, PlanningSession state, string[] sources, string focus) => new()
    {
        Json = PlanningCorpus.Transport(new JsonObject { ["binding"] = null, ["evidenceRequests"] = null, ["clarifications"] = null, ["reselectStep"] = null,
            ["discoveryRequests"] = new JsonArray(sources.Select(source => (JsonNode)new JsonObject { ["sourceId"] = source, ["cursor"] = null,
                ["query"] = null, ["operationIds"] = null, ["focusId"] = focus, ["justification"] = "Its described metadata may establish the required operation." }).ToArray()) },
            request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())
    };
    internal sealed class Source(string name, int count) : IMcpClientFactory
    {
        internal string Id => CapabilityDiscovery.SourceId(name);
        internal int Reads;
        internal List<McpToolInfo> Tools = Enumerable.Range(0, count).Select(i => new McpToolInfo { Name = "operation_" + i,
            Description = "Declared operation", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "string" } }).ToList();
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = name, Description = "Business operations" }];
        public Task<IMcpSession> GetClientAsync(string server, CancellationToken ct)
        { Reads++; var inner = new InMemoryMcpClientFactory(); inner.RegisterServer(name, new() { Tools = Tools }); return inner.GetClientAsync(server, ct); }
    }
    private sealed class Mixed(Source healthy, string failed) : ICapabilityCatalog
    {
        private readonly CapabilityDiscovery _inner = new(new() { McpClientFactory = healthy });
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => _inner.ListSourcesAsync(ct);
        public Task<CapabilityPage> ListAsync(string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) =>
            source == failed ? throw new IOException("PRIVATE_PROVIDER_DETAIL") : _inner.ListAsync(source, cursor, ct, query, producedArtifactKind);
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => _inner.ResolveAsync(summary, ct);
    }
    private sealed class Faulty(string id, string outcome) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new(id, "Source")]);
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => outcome switch
        {
            "unavailable" => throw new IOException("PRIVATE"), "timeout" => throw new OperationCanceledException("PRIVATE"),
            "changed" => throw new ArgumentException("PRIVATE"), "invalid" => throw new System.Text.Json.JsonException("PRIVATE"),
            "ownership" => Task.FromResult(new CapabilityPage("another-source", cursor, [], null)),
            _ => Task.FromResult(new CapabilityPage(source, cursor, [], cursor is null ? "next" : cursor, Query: query))
        };
    }
    private sealed class Cancelling(CancellationTokenSource cancellation) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string source, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
    }
}
