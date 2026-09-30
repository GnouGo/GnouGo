using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedCatalogOwnedBindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OriginalResponsesRecoverWithOwnedAssignmentRejectedBeforeGraphEmission()
    {
        var runtime = new Replay(); var planner = new HybridWorkflowPlanner(); PlanningSession? state = null;
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
        {
            runtime.Expected = entry!.AsObject(); var pending = runtime.State(entry["pendingSession"]!);
            var before = JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession);
            var usage = JsonSerializer.Serialize(pending.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            state = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, Ct);
            Assert.Equal(pending.ModelCalls, state.ModelCalls); Assert.Equal(pending.ReplanAttempts, state.ReplanAttempts);
            Assert.Equal(before, JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
            Assert.Null(state.PendingCall); Assert.Null(state.Graph); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
            if (state.ModelCalls < 7) Assert.Empty(state.Diagnostics);
            else Assert.Contains(state.Diagnostics, d => d.Code == "TASK_INPUT_HOST_OWNED" && d.Location == "/tasks/run_project_checks/inputs/runner");
        }
        Assert.Equal(8, state!.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Equal(8, runtime.Identities.Count);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Contains(state.Diagnostics, d => d.Code == "LLM_BUDGET_EXCEEDED");
        Assert.Contains(runtime.Recording["finalSession"]!["diagnostics"]!.AsArray(), d => d!["message"]!.ToString().StartsWith("CAPABILITY_BINDING_OVERRIDE:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyntheticOmissionInNewSessionReachesReviewWithCatalogRunnerAndOriginalContracts()
    {
        var runtime = new Replay(); var retained = runtime.State(runtime.Recording["finalSession"]!);
        // A new synthetic session, not a restart or budget reset of the real run.
        var state = new PlanningSession { Request = retained.Request, Requirements = retained.Requirements,
            Catalog = retained.Catalog, Discovery = retained.Discovery };
        state.Request.SessionId = "synthetic-owned-omission";
        // Reuse metadata receipts, not the stopped session's model inspection selections.
        state.Discovery.Inspections = null;
        var original = runtime.Recording["responses"]!.AsArray()[7]!["response"]!["json"]!["plan"]!.DeepClone();
        var expected = original.DeepClone();
        var task = expected["root"]!["tasks"]!.AsArray().Single(t => t!["id"]!.ToString() == "run_project_checks")!;
        task["inputs"]!.AsArray().Remove(task["inputs"]!.AsArray().Single(i => i!["name"]!.ToString() == "runner"));
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Single(runtime.Identities);
        Assert.Equal(0, runtime.MetadataReads); Assert.Null(state.ApprovedHash);
        Assert.NotNull(state.Plan);
        Assert.Equal(JsonSerializer.Serialize(expected.Deserialize(PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan),
            JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var capability = state.Catalog!.Capabilities.Single(c => c.Id == state.Plan.Root.Tasks.Single(t => t.Id == "run_project_checks").Operation);
        Assert.Equal("coding", capability.FixedInput["runner"]!.ToString());
        var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!);
        var agent = document.Workflows.Values.SelectMany(w => w.Steps).Single(s => s.Type == "agent.run");
        Assert.Equal("coding", agent.Input!["runner"]!.ToString());
        PlanningArtifactApproval.Verify(state);
        var recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(recovered);
        var hash = state.ComputeArtifactHash(); capability.FixedInput["runner"] = "different";
        Assert.NotEqual(hash, state.ComputeArtifactHash()); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
        Assert.Equal("copilot", original["root"]!["tasks"]!.AsArray().Single(t => t!["id"]!.ToString() == "run_project_checks")!["inputs"]!.AsArray().Single(i => i!["name"]!.ToString() == "runner")!["value"]!["text"]!.ToString());
    }

    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        private static JsonObject FileData(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CatalogOwnedBindings", name + ".json")))!.AsObject();
        internal readonly JsonObject Recording = FileData("retained-owned-binding");
        internal JsonObject Expected = null!;
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
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            MetadataReads++; return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (Expected is null) { Identities.Add(request.ClientRequestId!); return Task.FromResult(new LLMResponse { Json = FileData("synthetic-owned-omission")["proposal"]!.DeepClone() }); }
            var issued = Expected["pendingSession"]!["pendingCall"]!;
            Assert.Equal(issued["id"]!.ToString(), request.ClientRequestId);
            Assert.Equal(issued["request"]!["prompt"]!.ToString(), request.Prompt);
            Assert.True(JsonNode.DeepEquals(issued["request"]!["structuredOutputSchema"], request.StructuredOutputSchema));
            Assert.DoesNotContain(request.ClientRequestId!, Identities); Identities.Add(request.ClientRequestId!);
            return Task.FromResult(Expected["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval or execution");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
