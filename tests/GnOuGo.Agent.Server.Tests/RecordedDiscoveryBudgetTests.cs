using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedDiscoveryBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SevenDisplacementResponsesRetainTheirNullPlanStopAndOriginalRequests()
    {
        var runtime = new Replay("retained-discovery-displacement.json");
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
            await RecordedPlanCompilation.RetiredAsync(runtime.State(entry!["pendingSession"]!), runtime);
        Assert.Empty(runtime.Identities); Assert.Equal(0, runtime.MetadataReads);
    }

    [Fact]
    public async Task SixHistoricalResponsesKeepTheirOriginalNullPlanStopAndAccounting()
    {
        var runtime = new Replay("retained-discovery-incomplete.json");
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
            await RecordedPlanCompilation.RetiredAsync(runtime.State(entry!["pendingSession"]!), runtime);
        Assert.Empty(runtime.Identities); Assert.Equal(0, runtime.MetadataReads);
    }

    [Fact]
    public async Task EightPreviouslyIssuedDiscoveryResponsesRecoverWithoutChangingTheirSchemasOrAccounting()
    {
        var runtime = new Replay("retained-discovery-exhaustion.json");
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
            await RecordedPlanCompilation.RetiredAsync(runtime.State(entry!["pendingSession"]!), runtime);
        Assert.Empty(runtime.Identities); Assert.Equal(0, runtime.MetadataReads);
    }

    [Fact]
    public async Task NewlyIssuedSeventhRequestRejectsTheOriginalDiscoveryResponseWithoutAnotherTurn()
    {
        var runtime = new Replay { VerifyOriginalRequest = false };
        var entry = runtime.Recording["responses"]!.AsArray()[6]!;
        runtime.Expected = entry.AsObject();
        var state = runtime.State(entry["pendingSession"]!);
        state.IntentVersion = 2; if (state.Requirements is not null) state.Requirements.Inputs ??= state.Plan?.Inputs ?? []; state.PendingCall = null; state.ModelCalls = 6;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("DISCOVERY_NOT_ALLOWED", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(7, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, runtime.MetadataReads); Assert.Single(runtime.Identities); Assert.Null(state.Plan);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        restored = await planner.AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, Ct);
        Assert.Equal(7, restored.ModelCalls); Assert.Single(runtime.Identities);
    }

    private sealed class Replay(string fileName = "retained-discovery-exhaustion.json") : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "DiscoveryBudget", fileName)))!.AsObject();
        internal JsonObject Expected = null!;
        internal bool VerifyOriginalRequest = true;
        internal int MetadataReads;
        internal readonly List<string> Identities = [];
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;

        internal PlanningSession State(JsonNode compact)
        {
            var json = compact.DeepClone().AsObject();
            var pageCount = json["recordedPages"]!.GetValue<int>();
            var resolvedIds = json["recordedResolved"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var query = json["presentationQuery"]?.GetValue<string>();
            json.Remove("recordedPages"); json.Remove("recordedResolved"); json.Remove("presentationQuery");
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            state.Catalog = Recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
            state.Discovery = Discovery;
            state.Discovery.Pages = state.Discovery.Pages.Take(pageCount).ToList();
            state.Discovery.Resolved = state.Discovery.Resolved.Where(c => resolvedIds.Contains(c.Id)).ToList();
            state.Discovery.PresentationQuery = query;
            return state;
        }

        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("Already retained");
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("Already retained");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            MetadataReads++;
            return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) =>
            Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            Identities.Add(request.ClientRequestId!);
            if (VerifyOriginalRequest)
            {
                var original = Expected["pendingSession"]!["pendingCall"]!;
                Assert.Equal(original["id"]!.ToString(), request.ClientRequestId);
                Assert.True(JsonNode.DeepEquals(original["request"]!["structuredOutputSchema"], request.StructuredOutputSchema));
                Assert.Equal(original["request"]!["prompt"]!.ToString(), request.Prompt);
            }
            return Task.FromResult(Expected["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => throw new InvalidOperationException("No proposal was returned");
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No execution approval");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
