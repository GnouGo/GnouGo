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
    public async Task RetiredResponsesRequireRevisionAndRetainOriginalRequestsAndAccounting()
    {
        var replay = new Replay();
        foreach (var entry in replay.Recording["responses"]!.AsArray())
        {
            replay.Expected = entry!.AsObject();
            var pending = replay.State(entry["pendingSession"]!);
            await RecordedPlanCompilation.RetiredAsync(pending, replay);
        }
        Assert.Empty(replay.Identities);
    }

    [Fact]
    public async Task StaticEnumsPreserveObservedBusinessContractsWithoutPlanningApproval()
    {
        var runtime = new Replay();
        var retained = runtime.State(runtime.Recording["finalSession"]!);
        var proposal = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnumPlanning", "synthetic-corrected.json")))!["proposal"]!.Deserialize(GnOuGo.Planning.Examples.PlanningFixtureJsonContext.Default.TaskPlanFixture)!;
        var catalog = runtime.Recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
        foreach (var contract in runtime.Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!.Resolved)
            if (catalog.Capabilities.All(c => c.Id != contract.Id)) catalog.Capabilities.Add(contract);
        await Task.CompletedTask;
        var state = RecordedPlanCompilation.CompileStatic(proposal, catalog, retained.Request);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        VerifyBusinessIntent(state);
        Assert.Equal(0, state.ModelCalls);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
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
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) =>
            throw new InvalidOperationException("Retired planning recordings are read-only fixtures and cannot dispatch.");
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval or execution");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
