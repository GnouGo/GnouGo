using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedCheckedContractPlanningTests
{
    private static JsonObject RecordingFile() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CheckedContractPlanning", "retained-enum-projection.json")))!.AsObject();

    [Fact]
    public async Task OriginalFinalResponseReachesReviewWithoutAnotherModelCallOrWiderRepair()
    {
        var runtime = new Replay();
        var state = await runtime.Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Empty(state.Diagnostics); Assert.Null(state.ApprovedHash);
        Assert.Equal(5, runtime.Calls); Assert.Equal(5, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Equal(runtime.Recording["responses"]!.AsArray().Select(r => r!["id"]!.ToString()), runtime.RequestIds);
        var baseline = runtime.States[2]; var rejected = runtime.States[3];
        Assert.Equal(2, baseline.Diagnostics.Count(d => d.Code == "TASK_INPUT_TYPE"));
        Assert.Contains(rejected.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/inputs/pullRequestUrl/type/enum");
        Assert.Equal(JsonSerializer.Serialize(baseline.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(rejected.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(JsonSerializer.Serialize(baseline.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState), JsonSerializer.Serialize(rejected.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(baseline.RevisionScope, rejected.RevisionScope);
        Assert.True(JsonNode.DeepEquals(baseline.Request.Options, rejected.Request.Options));
        Assert.Equal(baseline.Request.MaxModelCalls, rejected.Request.MaxModelCalls);
        Assert.Equal(baseline.Request.MaxReplanAttempts, rejected.Request.MaxReplanAttempts);
        Assert.True(JsonNode.DeepEquals(runtime.Recording["plan"], JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        PlanningArtifactApproval.Verify(state);
        var recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(recovered);
        var before = recovered.ComputeArtifactHash();
        recovered.Plan!.Root.Tasks.Single(t => t.Id == "t_prepare_review").ResultType!.Fields.Single(f => f.Name == "event").Type.Enum = ["outside"];
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
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct) => Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor));
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
