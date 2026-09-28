using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedContractAwareRepairTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PlanJson(PlanningSession state) => JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
    private static string ReceiptJson(PlanningSession state)
    {
        var discovery = JsonSerializer.SerializeToNode(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState)!.AsObject();
        discovery.Remove("limitations"); // Final review records coverage findings; receipt content must stay identical.
        return discovery.ToJsonString();
    }

    [Fact]
    public async Task AllEightOriginalResponsesRetainTheTypeErrorsRejectedRewriteAndExhaustedBudget()
    {
        var runtime = new Replay(); var planner = new HybridWorkflowPlanner(); PlanningSession? state = null;
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
        {
            runtime.Expected = entry!.AsObject(); state = runtime.State(entry["pendingSession"]!);
            var baseline = PlanJson(state);
            var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
            var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            var scope = state.RevisionScope.ToArray(); var calls = state.ModelCalls; var repairs = state.ReplanAttempts;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
            Assert.Equal(calls, state.ModelCalls); Assert.Equal(repairs, state.ReplanAttempts);
            Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
            Assert.Null(state.PendingCall);
            if (calls < 7) Assert.Empty(state.Diagnostics);
            if (calls == 7)
            {
                Assert.Equal(new[] { "/tasks/add_inline_comment/inputs/startLine", "/tasks/add_inline_comment/inputs/startSide", "/tasks/run_checks_with_copilot/inputs/permissionMode" },
                    state.Diagnostics.Where(d => d.Code == "TASK_INPUT_TYPE").Select(d => d.Location));
                Assert.Contains(state.Diagnostics, d => d.Code == "TASK_DEPENDENCY_UNKNOWN" && d.Location == "/tasks/compare_pr/dependsOn");
            }
            if (calls == 8)
            {
                Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/format_inline_comment/resultType/fields");
                Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/parse_pr_url/objective");
                Assert.DoesNotContain(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/add_inline_comment/inputs");
                Assert.Equal(baseline, PlanJson(state)); Assert.Equal(scope, state.RevisionScope);
                Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
            }
        }
        state = await planner.AdvanceAsync(state!, new() { ExpectedRevision = state!.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(8, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "LLM_BUDGET_EXCEEDED"); Assert.Equal(8, runtime.Identities.Count);
        Assert.Null(state.Graph); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        // The original recording retains the old blanket input-list rejection as historical evidence.
        Assert.Contains(runtime.Recording["finalSession"]!["diagnostics"]!.AsArray(), d => d!["location"]!.ToString() == "/tasks/add_inline_comment/inputs");
    }

    [Fact]
    public async Task MinimalExplicitRepairRecoversTheIssuedRequestAndReachesReviewWithoutAnotherReservation()
    {
        var runtime = new Replay { Corrected = true }; runtime.Expected = runtime.Recording["responses"]!.AsArray()[7]!.AsObject();
        var state = runtime.State(runtime.Expected["pendingSession"]!);
        var calls = state.ModelCalls; var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var baseline = PlanJson(state); var receipts = ReceiptJson(state);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Empty(state.Diagnostics); Assert.Null(state.ApprovedHash); Assert.Null(state.PendingCall);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Single(runtime.Identities);
        Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(receipts, ReceiptJson(state));
        Assert.Contains(state.Discovery.Limitations, l => l.StartsWith("Discovery is incomplete:", StringComparison.Ordinal));
        Assert.NotEqual(baseline, PlanJson(state)); Assert.NotNull(state.Yaml);
        PlanningArtifactApproval.Verify(state);
        var recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(recovered); var hash = recovered.ComputeArtifactHash();
        recovered.Plan!.Root.Tasks[0].Objective += " changed intent";
        Assert.NotEqual(hash, recovered.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(recovered));
        Assert.Equal(0, runtime.MetadataReads);
    }

    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        private static JsonObject FileData(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ContractAwareRepair", name + ".json")))!.AsObject();
        internal readonly JsonObject Recording = FileData("retained-optional-repair");
        internal JsonObject Expected = null!;
        internal bool Corrected;
        internal int MetadataReads;
        internal readonly List<string> Identities = [];
        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;
        internal PlanningSession State(JsonNode compact)
        {
            var json = compact.DeepClone().AsObject();
            var pages = json["recordedPages"]!.GetValue<int>();
            var resolved = json["recordedResolved"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var ids = json["recordedCatalogIds"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var query = json["presentationQuery"]?.GetValue<string>();
            var inspections = json["recordedInspections"]?.DeepClone();
            foreach (var key in new[] { "recordedPages", "recordedResolved", "recordedCatalogIds", "presentationQuery", "recordedInspections" }) json.Remove(key);
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            state.Catalog = Recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
            state.Catalog.Capabilities.RemoveAll(c => !ids.Contains(c.Id));
            state.Discovery = Discovery; state.Discovery.Pages = state.Discovery.Pages.Take(pages).ToList();
            state.Discovery.Resolved.RemoveAll(c => !resolved.Contains(c.Id)); state.Discovery.PresentationQuery = query;
            state.Discovery.Inspections = inspections?.Deserialize(PlanningJsonContext.Default.ListPlanningDiscoveryRequest);
            return state;
        }
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("Catalog already retained");
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("Sources already retained");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null)
        {
            MetadataReads++; return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            var issued = Expected["pendingSession"]!["pendingCall"]!;
            Assert.Equal(issued["id"]!.ToString(), request.ClientRequestId);
            Assert.Equal(issued["request"]!["prompt"]!.ToString(), request.Prompt);
            Assert.True(JsonNode.DeepEquals(issued["request"]!["structuredOutputSchema"], request.StructuredOutputSchema));
            Assert.DoesNotContain(request.ClientRequestId!, Identities); Identities.Add(request.ClientRequestId!);
            return Task.FromResult(Corrected ? new LLMResponse { Json = FileData("synthetic-minimal-repair")["proposal"]!.DeepClone() }
                : Expected["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval or execution");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
