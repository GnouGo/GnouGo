using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedComposedOutputPlanningTests
{
    private static JsonObject RecordingFile() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ComposedOutputPlanning", "retained-composed-outputs.json")))!.AsObject();

    [Fact]
    public async Task RecordedBusinessPlanCompilesOfflineWithoutResumingHistoricalRequests()
    {
        var runtime = new Replay(); var state = await runtime.Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Message)));
        Assert.Null(state.ApprovedHash); Assert.Equal(0, runtime.Calls); Assert.Empty(runtime.RequestIds);
        Assert.True(JsonNode.DeepEquals(runtime.Recording["plan"], JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        PlanningArtifactApproval.Verify(state);
        var restored = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(restored);
        restored.Plan!.Root.Tasks[0].Objective += " changed";
        Assert.NotEqual(state.ComputeArtifactHash(), restored.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(restored));
    }

    internal sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = RecordingFile();

        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        internal readonly List<PlanningSession> States = [];
        internal readonly List<string> RequestIds = [];
        internal int Calls;
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;

        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(Recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!);
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(Discovery.Sources);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            var responses = Recording["responses"]!.AsArray();
            if (Calls >= responses.Count) throw new InvalidOperationException("No replacement response or live dispatch is permitted.");
            RequestIds.Add(request.ClientRequestId!);
            var entry = responses[Calls++]!;
            Assert.Equal(entry["id"]!.ToString(), request.ClientRequestId);
            Assert.True(JsonNode.DeepEquals(entry["schema"], request.StructuredOutputSchema));
            return Task.FromResult(entry["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("This replay never approves an external workflow.");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
        internal async Task<PlanningSession> Run()
        {
            return await RecordedPlanCompilation.CompileAsync(Recording);
    }
    }
}
