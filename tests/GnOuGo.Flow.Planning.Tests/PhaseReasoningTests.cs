using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PhaseReasoningTests
{
    [Theory]
    [InlineData("behavior_revision_scope_repair", "medium")]
    [InlineData("semantic_review_repair", "medium")]
    [InlineData("construction_schema_repair", "low")]
    public void CorrectionPagesRetainTheirPhaseProfile(string phase, string expected)
        => Assert.Equal(expected, PlanningGenerationPolicy.ReasoningFor(new(), phase));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrUnknownReasoningStopsBeforeProviderDispatch(bool known)
    {
        var client = new MetadataClient(known ? ["high"] : null);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client }, (_, _) => Task.CompletedTask);
        var state = TypedPlannerTests.Session();
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Equal(0, client.Calls);
        Assert.Null(result.Outcome); Assert.Equal("MODEL_REASONING_UNPROVEN", result.TechnicalStop!.Code);
        Assert.False(result.TechnicalStop.Unverifiable);
        Assert.Equal("not_dispatched", Assert.Single(result.RequestAccounting).Evidence);
        Assert.Empty(result.Construction.PendingCalls);
    }

    [Fact]
    public async Task VerifiedReasoningAndItsMetadataFingerprintArePersistedWithTheExactRequest()
    {
        var client = new MetadataClient(["low", "medium"]); PlanningSnapshot? checkpoint = null;
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client }, (state, _) => { checkpoint = PlanningContext.Clone(state); return Task.CompletedTask; });
        var state = TypedPlannerTests.Session();
        var call = PlanningModelCalls.Reserve(state, "behavior", "main", PlanningModelCalls.Request(state, "Pick a boundary", PlanningHoleRequests.Object(("choice", PlanningHoleRequests.Enum("a", "b")))));
        var hash = call.RequestHash;
        await runtime.CheckpointAsync(state, TestContext.Current.CancellationToken);
        var persisted = Assert.Single(checkpoint!.Construction.PendingCalls);
        Assert.Equal("medium", persisted.Request.Reasoning); Assert.Equal(hash, persisted.RequestHash);
        Assert.NotNull(persisted.ReasoningCapabilityFingerprint); Assert.Equal(0, client.Calls);
        Assert.Equal("medium", Assert.Single(checkpoint.RequestAccounting).Reasoning);
        var resolutions = client.Resolutions;
        await runtime.CheckpointAsync(checkpoint, TestContext.Current.CancellationToken);
        Assert.Equal(resolutions, client.Resolutions);
    }

    [Fact]
    public async Task MetadataFailureClearsTheEntireUndispatchedBatchWithoutRetryingLookup()
    {
        var client = new MetadataClient(["low", "medium"]) { FailOnResolution = 2 };
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client }, (_, _) => Task.CompletedTask);
        var state = TypedPlannerTests.Session();
        foreach (var owner in new[] { "a", "b" })
            PlanningModelCalls.Reserve(state, "construction", owner, PlanningModelCalls.Request(state, "Select", PlanningHoleRequests.Object(("choice", PlanningHoleRequests.Enum("a", "b")))));
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => runtime.CheckpointAsync(state, TestContext.Current.CancellationToken));
        Assert.Equal("MODEL_METADATA_UNAVAILABLE", error.Code);
        Assert.Empty(state.Construction.PendingCalls);
        Assert.All(state.RequestAccounting, a => Assert.Equal("not_dispatched", a.Evidence));
        await runtime.CheckpointAsync(state, TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Resolutions); Assert.Equal(0, client.Calls);
    }

    private sealed class MetadataClient(IReadOnlyList<string>? levels) : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls, Resolutions;
        internal int FailOnResolution;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject() }); }
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct)
        {
            if (++Resolutions == FailOnResolution) throw new IOException("metadata unavailable");
            return Task.FromResult(levels);
        }
    }
}
