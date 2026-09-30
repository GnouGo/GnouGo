using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedEnumPlanningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OriginalResponsesRecoverWithOriginalSchemasAndRetainTheTypeFailures()
    {
        var runtime = new Replay(); var planner = new HybridWorkflowPlanner(); PlanningSession? state = null;
        foreach (var entry in runtime.Recording["responses"]!.AsArray())
        {
            runtime.Expected = entry!.AsObject(); var pending = runtime.State(entry["pendingSession"]!);
            var before = JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession);
            state = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, Ct);
            Assert.Equal(pending.ModelCalls, state.ModelCalls); Assert.Equal(pending.ReplanAttempts, state.ReplanAttempts);
            Assert.Equal(before, JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(JsonSerializer.Serialize(pending.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot),
                JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
            Assert.Null(state.PendingCall); Assert.Null(state.Graph); Assert.Null(state.Yaml);
            if (state.ModelCalls == 5) Assert.Contains(state.Diagnostics, d => d.Code == "DISCOVERY_NO_PROGRESS");
            else if (state.ModelCalls < 7) Assert.Empty(state.Diagnostics);
        }
        Assert.Equal(8, state!.Diagnostics.Count); Assert.Equal(5, state.Diagnostics.Count(d => d.Code == "TASK_INPUT_TYPE"));
        Assert.Equal(3, state.Diagnostics.Count(d => d.Code == "TASK_TRANSFORM_CONSTRAINT"));
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(7, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Equal(7, runtime.Identities.Count); // Never refund historical reservations.
        Assert.Contains(runtime.Recording["history"]!.AsArray(), h => h!["revision"]!.GetValue<int>() == 12 && h["diagnostics"]!.AsArray().Count == 1);
    }

    [Fact]
    public async Task NewIssuedSchemaRejectsInventedLiteralsAndSyntheticRegenerationReachesReview()
    {
        var runtime = new Replay(); var retained = runtime.State(runtime.Recording["finalSession"]!);
        var state = new PlanningSession { Request = retained.Request, Requirements = retained.Requirements, Catalog = retained.Catalog, Discovery = retained.Discovery };
        state.Request.SessionId = "synthetic-enum-regeneration";
        state.Discovery.Inspections = null;
        runtime.NextProposal = runtime.Recording["responses"]!.AsArray()[6]!["response"]!["json"]!.DeepClone().AsObject();
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Null(state.Plan); Assert.Null(state.Graph);
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_RESPONSE_INVALID");
        runtime.NextProposal = null;
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.NotNull(state.Yaml); Assert.Equal(0, runtime.MetadataReads);
        VerifyBusinessIntent(state);
        PlanningArtifactApproval.Verify(state);
        var recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(recovered);
        recovered.Plan!.Root.Tasks.Single(t => t.Id == "run_project_checks").Objective += " Changed";
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(recovered));
        // A schema-valid filesystem substitution is not an equivalent check operation.
        state.Plan!.Root.Tasks.Single(t => t.Id == "run_project_checks").Operation = "cap_5c01f9aa688163d23a30ff8b";
        Assert.ThrowsAny<Exception>(() => VerifyBusinessIntent(state));
    }

    private static void VerifyBusinessIntent(PlanningSession state)
    {
        var tasks = state.Plan!.Root.Tasks;
        var checks = tasks.Single(t => t.Id == "run_project_checks");
        var contract = state.Catalog!.Capabilities.Single(c => c.Id == checks.Operation);
        Assert.Equal("copilot_one_shot", contract.Method);
        Assert.Equal("clone_repo", checks.Inputs.Single(i => i.Name == "projectRoot").Value.Source);
        Assert.Equal("projectRootRelative", checks.Inputs.Single(i => i.Name == "projectRoot").Value.Port);
        Assert.Equal("deny", checks.Inputs.Single(i => i.Name == "permissionMode").Value.Text);
        var prompt = tasks.Single(t => t.Id == "checks_command").Outputs.Single().Value.Text!;
        foreach (var required in new[] { "dependencies", "lint", "unit", "integration", "exit code", "permission refusal" }) Assert.Contains(required, prompt);
        Assert.Contains("toolExecutions", tasks.Single(t => t.Id == "review_context").Objective);
        var cleanup = state.Plan.Root.Always.Single(t => t.Id == "cleanup_clone");
        Assert.Equal("delete_directory", cleanup.Inputs.Single(i => i.Name == "commandName").Value.Text);
        var encoding = state.Plan.Root.Always.Single(t => t.Id == "cleanup_command").Outputs.Single().Value;
        Assert.Equal("json", encoding.Kind); Assert.Equal("path", encoding.Items.Single().Members.Single().Name);
        Assert.Equal("clone_location", encoding.Items.Single().Members.Single().Value.Source);
        Assert.Equal("targetDirectory", encoding.Items.Single().Members.Single().Value.Port);
        var body = tasks.Single(t => t.Id == "publish_inline_comments").Body!;
        var output = body.Tasks.Single(t => t.Id == "inline_comment").ResultType!;
        foreach (var name in new[] { "side", "startSide" }) Assert.Equal(new[] { "LEFT", "RIGHT" }, output.Fields.Single(f => f.Name == name).Type.Enum);
        Assert.Equal(new[] { "FILE", "LINE" }, output.Fields.Single(f => f.Name == "subjectType").Type.Enum);
        var comment = body.Tasks.Single(t => t.Id == "add_inline_comment");
        foreach (var port in new[] { "body", "path", "line", "startLine", "side", "startSide", "subjectType" })
        { var value = comment.Inputs.Single(i => i.Name == port).Value; Assert.Equal("inline_comment", value.Source); Assert.Equal(port, value.Port); }
    }
    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        private static JsonObject FileData(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnumPlanning", name + ".json")))!.AsObject();
        internal readonly JsonObject Recording = FileData("retained-enums");
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
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            MetadataReads++; return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (Expected is null) { Identities.Add(request.ClientRequestId!); Requests.Add(request); return Task.FromResult(new LLMResponse { Json = (NextProposal ?? FileData("synthetic-corrected")["proposal"]!).DeepClone() }); }
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
