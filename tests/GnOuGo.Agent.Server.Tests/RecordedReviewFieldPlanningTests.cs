using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedReviewFieldPlanningTests
{
    private static JsonObject Recording(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReviewPlanning", name + ".json")))!.AsObject();

    [Fact]
    public async Task RetainedResponsesReproduceReferenceFailureRejectedRepairAndAllFiveTypeErrors()
    {
        var runtime = new Replay(historical: true);
        var state = await runtime.Run();
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Null(state.Graph); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(0, runtime.Calls); Assert.Empty(runtime.RequestIds);
        Assert.Equal(5, state.Diagnostics.Count(d => d.Code == "TASK_INPUT_TYPE"));
    }

    [Fact]
    public async Task ExplicitFieldBindingsReachReviewWithActualContractsAndNoAdditionalInferencePhase()
    {
        var runtime = new Replay(historical: false);
        var state = await runtime.Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + "@" + d.Location + ": " + d.Message)));
        Assert.Empty(state.Diagnostics); Assert.NotNull(state.Graph); Assert.NotNull(state.Yaml);
        Assert.Equal(0, runtime.Calls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        PlanningArtifactApproval.Verify(state);
    }

    internal sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = RecordedReviewFieldPlanningTests.Recording("retained-field-bindings");
        private readonly bool historical;
        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        internal readonly List<PlanningSession> States = [];
        internal readonly List<string> RequestIds = [];
        internal int Calls;
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;
        internal Replay(bool historical) => this.historical = historical;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(Recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!);
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(Discovery.Sources);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            var responses = Recording["responses"]!.AsArray();
            if (Calls >= (historical ? responses.Count : 3)) throw new InvalidOperationException("No replacement response or live dispatch is permitted.");
            RequestIds.Add(request.ClientRequestId!);
            var entry = responses[Calls++]!;
            if (historical)
            {
                Assert.Equal(entry["id"]!.ToString(), request.ClientRequestId);
                Assert.True(JsonNode.DeepEquals(entry["schema"], request.StructuredOutputSchema));
            }
            if (!historical && Calls == 3) return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(
                RecordedReviewFieldPlanningTests.Recording("synthetic-field-correction")["proposal"], request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
            return Task.FromResult(entry["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("This replay never approves an external workflow.");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
        internal async Task<PlanningSession> Run()
        {
            var plan = historical ? null : RecordedReviewFieldPlanningTests.Recording("synthetic-field-correction")["proposal"]!["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan);
            return await RecordedPlanCompilation.CompileAsync(Recording, plan);
    }
    }
}
