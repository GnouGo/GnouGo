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
    public async Task StaticCatalogOwnedBindingsCompileWithoutGrantingArtifactApproval()
    {
        var runtime = new Replay(); var retained = runtime.State(runtime.Recording["finalSession"]!);
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CatalogOwnedBindings", "synthetic-owned-omission.json")))!["proposal"]!["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        foreach (var task in TaskPlanRevisions.Tasks(plan).Where(t => t.Kind == "transform" && t.Mode is null)) task.Mode = "interpret";
        var state = RecordedPlanCompilation.CompileStatic(new() { Plan = plan, Requirements = retained.Requirements }, retained.Catalog!, retained.Request);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        var capability = state.Catalog!.Capabilities.Single(c => c.Id == state.Plan!.Root.Tasks.Single(t => t.Id == "run_project_checks").Operation);
        Assert.Equal("coding", capability.FixedInput["runner"]!.ToString());
        var document = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!);
        Assert.Equal("coding", document.Workflows.Values.SelectMany(w => w.Steps).Single(s => s.Type == "agent.run").Input!["runner"]!.ToString());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
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
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) =>
            throw new InvalidOperationException("Retired planning recordings are read-only fixtures and cannot dispatch.");
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval or execution");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
