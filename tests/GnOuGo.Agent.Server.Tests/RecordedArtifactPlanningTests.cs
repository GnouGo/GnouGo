using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedArtifactPlanningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Locations = ["/tasks/checkout_pull_request/inputs/projectRoot", "/tasks/fetch_pull_request_ref/inputs/projectRoot"];

    [Fact]
    public async Task RecordedResponsesKeepTheirIdentitiesAndDiagnoseTheWrongBusinessFields()
    {
        var runtime = new Replay(); var planner = new HybridWorkflowPlanner(); PlanningSession? state = null;
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
        {
            runtime.Expected = entry!.AsObject(); var pending = runtime.State(entry["pendingSession"]!);
            var before = JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession);
            state = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, Ct);
            Assert.Equal(pending.ModelCalls, state.ModelCalls); Assert.Equal(pending.ReplanAttempts, state.ReplanAttempts);
            Assert.Equal(before, JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(JsonSerializer.Serialize(pending.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
            Assert.Null(state.PendingCall); Assert.Null(state.Graph); Assert.Null(state.Yaml);
        }
        Assert.Equal(5, runtime.Identities.Count); Assert.Equal(5, state!.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(Locations, state.Diagnostics.Select(d => d.Location));
        Assert.All(state.Diagnostics, d => Assert.Equal("TASK_ARTIFACT_BINDING", d.Code));
        Assert.Equal(Locations, state.RevisionScope.Order(StringComparer.Ordinal));
        Assert.Contains(runtime.Recording["finalSession"]!["diagnostics"]!.AsArray(), d => d!["message"]!.ToString().Contains("SCHEMA_INVALID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitMinimalRepairReachesReviewAndKeepsApprovalAndRepairBoundaries()
    {
        var runtime = new Replay(); var planner = new HybridWorkflowPlanner();
        var last = runtime.Recording["responses"]!.AsArray().Last()!;
        runtime.Expected = last.AsObject(); var pending = runtime.State(last["pendingSession"]!);
        var state = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, Ct);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var receipts = JsonSerializer.Serialize(state.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage);
        runtime.Expected = null;
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var repaired = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(repaired.Status == PlanningStatus.FinalReview, string.Join("; ", repaired.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(6, repaired.ModelCalls); Assert.Equal(2, repaired.ReplanAttempts); Assert.Null(repaired.ApprovedHash);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(receipts, JsonSerializer.Serialize(repaired.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
        Assert.Equal(2, runtime.Identities.Count); Assert.Equal(0, runtime.MetadataReads);
        foreach (var id in new[] { "fetch_pull_request_ref", "checkout_pull_request" })
            Assert.Equal("projectRootRelative", repaired.Plan!.Root.Tasks.Single(t => t.Id == id).Inputs.Single(i => i.Name == "projectRoot").Value.Port);
        Assert.NotNull(repaired.Yaml); PlanningArtifactApproval.Verify(repaired);
        var recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(repaired, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(recovered);
        recovered.Plan!.Root.Tasks.Single(t => t.Id == "fetch_pull_request_ref").Inputs.Single(i => i.Name == "projectRoot").Value.Port = "repositoryRoot";
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(recovered));
        // The same repair cannot change a sibling objective, even after recovery.
        runtime.NextProposal = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtifactPlanning", "synthetic-corrected.json")))!["proposal"]!.AsObject();
        runtime.NextProposal["plan"]!["root"]!["tasks"]![0]!["objective"] = "Changed intent";
        var rejected = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Contains(rejected.Diagnostics, d => d.Code == "PLANNING_RESPONSE_INVALID" && d.Location == "/plan");
        Assert.Equal(baseline, JsonSerializer.Serialize(rejected.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(receipts, JsonSerializer.Serialize(rejected.Discovery.Pages, PlanningJsonContext.Default.ListCapabilityPage));
        Assert.Equal(6, rejected.ModelCalls); Assert.Equal(2, rejected.ReplanAttempts);
    }
    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        private static JsonObject FileData(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtifactPlanning", name + ".json")))!.AsObject();
        internal readonly JsonObject Recording = FileData("retained-artifacts");
        internal JsonObject? Expected;
        internal JsonObject? NextProposal;
        internal List<LLMRequest> Requests = [];
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
            if (Expected is null)
            {
                Identities.Add(request.ClientRequestId!); Requests.Add(request);
                // An attempted whole-plan rewrite remains an explicit invalid response.
                if (NextProposal is not null) return Task.FromResult(new LLMResponse { Json = NextProposal.DeepClone() });
                var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
                var tasks = FileData("synthetic-corrected")["proposal"]!["plan"]!["root"]!["tasks"]!.AsArray();
                var edits = new JsonArray();
                foreach (var location in Locations)
                {
                    var id = location.Split('/')[2];
                    var value = tasks.Single(t => t!["id"]!.ToString() == id)!["inputs"]!.AsArray().Single(i => i!["name"]!.ToString() == "projectRoot")!["value"]!;
                    var slot = context["repair"]!["slots"]!.AsArray().Single(s => s!["location"]!.ToString() == location)!;
                    edits.Add((JsonNode)new JsonObject { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = value.DeepClone() });
                }
                var response = new JsonObject { ["discoveryRequests"] = null, ["patch"] = new JsonObject { ["edits"] = edits } };
                return Task.FromResult(new LLMResponse { Json = GnOuGo.Planning.Examples.PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
            }
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
