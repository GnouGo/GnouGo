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
    public async Task OriginalFinalResponseReachesReviewWithoutAnotherModelCallOrWiderRepair()
    {
        var runtime = new Replay();
        var state = await runtime.Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Empty(state.Diagnostics); Assert.Null(state.ApprovedHash);
        var original = runtime.Recording["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal(3, runtime.Calls); Assert.Equal(3, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(runtime.Recording["responses"]!.AsArray().Select(r => r!["id"]!.ToString()), runtime.RequestIds);
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
        recovered.Plan!.Root.Tasks.Single(t => t.Id == "publish_review").Body!.Outputs[0].Value.Members[0].Value.Text = "changed";
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
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public ICapabilityCatalog Capabilities => this;

        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(Recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!);
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(Discovery.Sources);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null) => Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor));
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
            var state = Recording["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            var responses = Recording["responses"]!.AsArray();
            // Redelivery of recorded receipts must preserve the already charged cumulative snapshot.
            state.Usage = Recording["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!.Usage;
            // The saved user-selected allowance admitted the original TaskPlan.
            state.Request.Generation = responses[2]!["generation"]!.Deserialize(PlanningJsonContext.Default.PlanningGenerationOptions)!;
            var planner = new HybridWorkflowPlanner();
            for (var i = 0; i < 8 && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status); i++)
            {
                if (Calls < responses.Count)
                {
                    var entry = responses[Calls]!; var purpose = entry["purpose"]!.ToString();
                    state.ModelCalls++; if (purpose == "replan") state.ReplanAttempts++;
                    state.PendingCall = new() { Id = entry["id"]!.ToString(), Purpose = purpose,
                        Request = new() { ClientRequestId = entry["id"]!.ToString(), StructuredOutputSchema = entry["schema"]!.DeepClone() } };
                    state.Request.Generation = entry["generation"]!.Deserialize(PlanningJsonContext.Default.PlanningGenerationOptions)!;
                }
                state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
                state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, this, TestContext.Current.CancellationToken);
                States.Add(state);
            }
            return state;
        }
    }
}
