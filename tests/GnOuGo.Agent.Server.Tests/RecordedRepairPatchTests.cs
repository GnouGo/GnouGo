using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedRepairPatchTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PlanJson(PlanningSession s) => JsonSerializer.Serialize(s.Plan, PlanningJsonContext.Default.TaskPlan);
    private static string Receipts(PlanningSession s) => JsonSerializer.Serialize(s.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);
    private static int Estimate(LLMRequest request) => (Bytes(request.Prompt) + Bytes(request.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256;

    [Fact]
    public async Task SevenOriginalResponsesRetainBothRejectedWholePlanRepairs()
    {
        var replay = new Replay(); var planner = new HybridWorkflowPlanner();
        foreach (var entry in replay.Recording["responses"]!.AsArray())
        {
            replay.Expected = entry!.AsObject(); var state = replay.State(entry["pendingSession"]!);
            var baseline = PlanJson(state); var receipts = Receipts(state); var scope = state.RevisionScope.ToArray();
            var calls = state.ModelCalls; var repairs = state.ReplanAttempts; var usage = state.Usage;
            var result = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, replay, Ct);
            Assert.Equal(calls, result.ModelCalls); Assert.Equal(repairs, result.ReplanAttempts); Assert.Null(result.PendingCall);
            Assert.Equal(JsonSerializer.Serialize(usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), JsonSerializer.Serialize(result.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
            if (calls == 5)
            {
                Assert.Equal(new[] { "/tasks/add_inline_comment/inputs/startLine", "/tasks/compare_refs/inputs/baseRef", "/tasks/compare_refs/inputs/headRef" },
                    result.Diagnostics.Where(d => d.Code == "TASK_INPUT_TYPE").Select(d => d.Location));
                Assert.Equal(6, result.RevisionScope.Count);
            }
            if (calls >= 6)
            {
                Assert.Contains(result.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/clone_once/objective");
                Assert.Equal(baseline, PlanJson(result)); Assert.Equal(scope, result.RevisionScope); Assert.Equal(receipts, Receipts(result));
                Assert.Null(result.Graph); Assert.Null(result.Yaml); Assert.Null(result.ApprovedHash);
            }
        }
        Assert.Equal(7, replay.Requests.Count); Assert.Equal(7, replay.Requests.Select(r => r.ClientRequestId).Distinct().Count());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ThreeExplicitEditsReachReviewWithLessContextAndNoAdditionalModelTurn(int index)
    {
        var replay = new Replay { Patch = true }; var original = replay.Recording["responses"]![index]!;
        var recorded = replay.State(original["pendingSession"]!);
        var old = recorded.PendingCall!.Request; var oldResponse = original["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!;
        var oldJson = oldResponse.Json ?? JsonNode.Parse(oldResponse.Text)!;
        // A separately identified offline candidate, never a rewritten/resumed live session.
        var state = replay.State(original["pendingSession"]!); state.PendingCall = null; state.ModelCalls--; state.ReplanAttempts--;
        state.Request.SessionId = "synthetic-patch-" + index; state.Request.Generation.MaxInputTokensPerRequest = 24000;
        var baseline = PlanJson(state); var originalScope = state.RevisionScope.ToArray(); var receipts = Receipts(state);
        var calls = state.ModelCalls; var repairs = state.ReplanAttempts; var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var planner = new HybridWorkflowPlanner();
        var result = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, replay, Ct);
        Assert.True(result.Status == PlanningStatus.FinalReview, string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var request = Assert.Single(replay.Requests); Assert.Null(result.ApprovedHash); Assert.Empty(result.Diagnostics);
        Assert.Equal(calls + 1, result.ModelCalls); Assert.Equal(repairs + 1, result.ReplanAttempts); Assert.Equal(8, result.Request.MaxModelCalls); Assert.Equal(2, result.Request.MaxReplanAttempts);
        Assert.Equal(usage, JsonSerializer.Serialize(result.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)); Assert.Equal(0, replay.MetadataReads);
        Assert.Equal(baseline, PlanJson(state)); Assert.Equal(receipts, Receipts(state)); Assert.Equal(originalScope, state.RevisionScope);
        Assert.False(request.StructuredOutputSchema!["properties"]!.AsObject().ContainsKey("plan"));
        var contextSizes = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!.AsObject();
        output.WriteLine(string.Join("; ", contextSizes.Select(p => p.Key + " bytes=" + Bytes(p.Value?.ToJsonString() ?? "null"))));
        Assert.True(Estimate(request) <= Estimate(old) / 2, $"Complete tokens: {Estimate(old)} -> {Estimate(request)}; prompt bytes={Bytes(request.Prompt)}; schema bytes={Bytes(request.StructuredOutputSchema!.ToJsonString())}"); Assert.True(Estimate(request) < 24000);
        Assert.True(Bytes(replay.PatchResponse!.ToJsonString()) <= Bytes(oldJson.ToJsonString()) / 5);
        var before = JsonNode.Parse(baseline)!; var after = JsonSerializer.SerializeToNode(result.Plan, PlanningJsonContext.Default.TaskPlan)!;
        // Independent diff oracle: only the optional binding and two nullability leaves changed.
        foreach (var task in Tasks(before))
        {
            if (task["id"]!.ToString() == "add_inline_comment")
                foreach (var input in task["inputs"]!.AsArray().Where(i => i!["name"]!.ToString() == "startLine").ToArray()) task["inputs"]!.AsArray().Remove(input);
            if (task["id"]!.ToString() == "pr_refs")
                foreach (var field in task["resultType"]!["fields"]!.AsArray().Where(f => f!["name"]!.ToString() is "baseRef" or "headSha")) field!["type"]!["nullable"] = false;
        }
        Assert.True(JsonNode.DeepEquals(before, after));
        PlanningArtifactApproval.Verify(result);
        var hash = result.ComputeArtifactHash(); result.Plan!.Root.Tasks[0].Objective += " changed";
        Assert.NotEqual(hash, result.ComputeArtifactHash()); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(result));

        // Simulate recovery after the request/receipt checkpoint: no new reservation or schema.
        var recovering = replay.Pending!; var recoveredRuntime = new Replay { Patch = true, Issued = request };
        var recovered = await planner.AdvanceAsync(recovering, new() { ExpectedRevision = recovering.Revision }, recoveredRuntime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Equal(calls + 1, recovered.ModelCalls); Assert.Equal(repairs + 1, recovered.ReplanAttempts);
        Assert.Equal(request.ClientRequestId, Assert.Single(recoveredRuntime.Requests).ClientRequestId);
        Assert.Equal(0, recoveredRuntime.MetadataReads); PlanningArtifactApproval.Verify(recovered);
        output.WriteLine($"repair={index - 4}; prompt bytes {Bytes(old.Prompt)} -> {Bytes(request.Prompt)}; schema bytes {Bytes(old.StructuredOutputSchema!.ToJsonString())} -> {Bytes(request.StructuredOutputSchema.ToJsonString())}; complete tokens {Estimate(old)} -> {Estimate(request)}; response bytes {Bytes(oldJson.ToJsonString())} -> {Bytes(replay.PatchResponse.ToJsonString())}; scripted calls=1; metadata reads=0; final calls={recovered.ModelCalls}, repairs={recovered.ReplanAttempts}.");
    }

    private static IEnumerable<JsonObject> Tasks(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("id") && obj.ContainsKey("kind")) yield return obj;
            foreach (var (_, value) in obj) if (value is not null) foreach (var task in Tasks(value)) yield return task;
        }
        else if (node is JsonArray array) foreach (var value in array) if (value is not null) foreach (var task in Tasks(value)) yield return task;
    }

    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RepairPatch", "retained-repair.json")))!.AsObject();
        internal JsonObject? Expected, PatchResponse;
        internal bool Patch;
        internal int MetadataReads;
        internal PlanningSession? Pending;
        internal LLMRequest? Issued;
        internal readonly List<LLMRequest> Requests = [];
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
        { MetadataReads++; return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            Requests.Add(request);
            if (!Patch)
            {
                var issued = Expected!["pendingSession"]!["pendingCall"]!;
                Assert.Equal(issued["id"]!.ToString(), request.ClientRequestId); Assert.Equal(issued["request"]!["prompt"]!.ToString(), request.Prompt);
                Assert.True(JsonNode.DeepEquals(issued["request"]!["structuredOutputSchema"], request.StructuredOutputSchema));
                return Task.FromResult(Expected["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
            }
            if (Issued is not null)
            {
                Assert.Equal(Issued.Prompt, request.Prompt); Assert.Equal(Issued.ClientRequestId, request.ClientRequestId);
                Assert.True(JsonNode.DeepEquals(Issued.StructuredOutputSchema, request.StructuredOutputSchema));
            }
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
            Assert.False(context.AsObject().ContainsKey("taskPlan"));
            var slots = context["repair"]!["slots"]!.AsArray(); Assert.Equal(6, slots.Count);
            JsonObject Edit(string location, string action)
            {
                var edit = new JsonObject { ["slot"] = slots.Single(s => s!["location"]!.ToString() == location)!["id"]!.DeepClone(), ["action"] = action };
                if (action == "replace") edit["value"] = false;
                return edit;
            }
            PatchResponse = new() { ["discoveryRequests"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray(
                Edit("/tasks/add_inline_comment/inputs/startLine", "remove"),
                Edit("/tasks/pr_refs/resultType/fields/baseRef/type/nullable", "replace"),
                Edit("/tasks/pr_refs/resultType/fields/headSha/type/nullable", "replace")) } };
            return Task.FromResult(new LLMResponse { Json = PatchResponse.DeepClone() });
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval or execution");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct)
        {
            if (session.PendingCall is not null) Pending = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
            return Task.CompletedTask;
        }
    }
}
