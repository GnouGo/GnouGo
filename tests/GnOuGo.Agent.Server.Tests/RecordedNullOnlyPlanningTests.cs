using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedNullOnlyPlanningTests
{
    private static JsonObject RecordingFile() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NullOnlyPlanning", "retained-null-outputs.json")))!.AsObject();

    [Fact]
    public async Task OriginalFinalResponseReachesReviewWithoutAnotherModelCallOrWiderRepair()
    {
        var runtime = new Replay();
        var state = await runtime.Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Empty(state.Diagnostics); Assert.Null(state.ApprovedHash); Assert.Null(state.PendingCall);
        var original = runtime.Recording["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal(3, runtime.Calls); Assert.Equal(4, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(runtime.CompletedResponses.Select(r => r["id"]!.ToString()), runtime.RequestIds);
        var missing = Assert.Single(runtime.Recording["responses"]!.AsArray(), r => r!["response"] is null)!;
        Assert.DoesNotContain(missing["id"]!.ToString(), runtime.RequestIds);
        Assert.Contains(runtime.Recording["sessions"]!.AsArray(), s => s!["pendingId"]?.ToString() == missing["id"]!.ToString() && s["diagnostics"]!.ToJsonString().Contains("MODEL_DISPATCH_UNVERIFIABLE", StringComparison.Ordinal));
        Assert.Empty(state.RevisionScope); Assert.Equal(original.RevisionScope, state.RevisionScope);
        Assert.Equal(JsonSerializer.Serialize(original.Request, PlanningJsonContext.Default.PlanningRequest), JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest));
        Assert.Equal(JsonSerializer.Serialize(original.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        var originalDiscovery = JsonSerializer.SerializeToNode(original.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState)!.AsObject();
        var actualDiscovery = JsonSerializer.SerializeToNode(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState)!.AsObject();
        // Final review derives discovery limitations; the originally stopped session never reached that step.
        originalDiscovery.Remove("limitations"); actualDiscovery.Remove("limitations");
        Assert.True(JsonNode.DeepEquals(originalDiscovery, actualDiscovery));
        Assert.All(original.Discovery.Limitations, limitation => Assert.Contains(limitation, state.Discovery.Limitations));
        Assert.Equal(new[] { "Discovery is incomplete: 5 sources were not inspected.", "Additional operation pages remain uninspected." }, state.Discovery.Limitations.Except(original.Discovery.Limitations));
        Assert.True(JsonNode.DeepEquals(runtime.Recording["plan"], JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        // The stopped session persisted no accepted graph; approval verification recompiles its unchanged plan.
        Assert.Null(original.Graph); Assert.NotNull(state.Graph);
        PlanningArtifactApproval.Verify(state);
        var recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(recovered);
        var before = recovered.ComputeArtifactHash();
        var gate = recovered.Plan!.Root.Tasks.Single(t => t.Id == "compare_page2_gate");
        gate.Otherwise!.Outputs.Single(o => o.Name == "nextCursor").Value.Kind = "string";
        gate.Otherwise.Outputs.Single(o => o.Name == "nextCursor").Value.Text = "changed";
        Assert.NotEqual(before, recovered.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(recovered));
    }

    internal sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = RecordingFile();

        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        internal readonly List<PlanningSession> States = [];
        internal readonly List<string> RequestIds = [];
        internal int Calls;
        internal JsonObject[] CompletedResponses => Recording["responses"]!.AsArray().OfType<JsonObject>().Where(r => r["response"] is not null).ToArray();
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;

        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(Recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!);
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(Discovery.Sources);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            var responses = CompletedResponses;
            if (Calls >= responses.Length) throw new InvalidOperationException("No replacement response or live dispatch is permitted.");
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
            PlanningSession? state = null;
            var planner = new HybridWorkflowPlanner();
            foreach (var entry in CompletedResponses)
            {
                // Recover the original pending checkpoint. The third attempt has no receipt:
                // do not invent one or retry it. Its charged reservation survives in call four.
                state = entry["pendingSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
                state.Usage = Recording["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!.Usage;
                state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
                state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, this, TestContext.Current.CancellationToken);
                States.Add(state);
            }
            return state!;
        }
    }
}
