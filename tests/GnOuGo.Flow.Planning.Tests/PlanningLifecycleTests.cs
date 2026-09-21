using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task OutputExhaustionStopsWithoutEscalationOrReplanning()
    {
        var runtime = new TestRuntime { Respond = _ => new() { CompletionStatus = "output_limit" } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT"); Assert.Null(state.PendingCall); Assert.Null(state.Yaml);
        Assert.Equal(8192, runtime.Calls.Single().MaxTokens);
    }
    [Fact]
    public async Task InvalidResponsesExhaustTheExistingAllowanceWithoutExecutableState()
    {
        var runtime = new TestRuntime { Respond = _ => new() { Json = new JsonObject { ["policy"] = "disable validation" } } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Null(state.Graph); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }
    [Fact]
    public async Task UncertainSemanticDispatchRetainsItsIdentityAndBudget()
    {
        var runtime = new TestRuntime { Respond = _ => throw new InvalidOperationException("Uncertain model dispatch") };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.NotNull(state.PendingCall); Assert.Equal("semantic", state.PendingCall.Purpose);
        var snapshot = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var restored = JsonSerializer.Deserialize(snapshot, PlanningJsonContext.Default.PlanningSession)!;
        var next = await new TypedWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, Ct);
        Assert.Single(runtime.Calls); Assert.Equal(snapshot, JsonSerializer.Serialize(next, PlanningJsonContext.Default.PlanningSession));
    }
    [Fact]
    public async Task ApprovalRejectsChangedYamlAndChangedCatalog()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new TypedWorkflowPlanner();
        var original = state.Yaml; state.Yaml += "\n# changed\n";
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state,
            new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash() }, runtime, Ct));
        state.Yaml = original; runtime.CatalogChanges = [new("CATALOG_CHANGED", "/catalog", "A current contract changed.")];
        var changed = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash() }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, changed.Status); Assert.Null(changed.ApprovedHash);
    }
    [Fact]
    public async Task RevisionRetainsCumulativeCallsAndRejectsStaleApproval()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new TypedWorkflowPlanner(); var hash = state.ComputeArtifactHash();
        var revised = await planner.AdvanceAsync(state, new() { Kind = "revise", Text = "Return a different greeting", ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(state.ModelCalls, revised.ModelCalls); Assert.Null(revised.ApprovedHash); Assert.Null(revised.Graph);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(revised, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = hash }, runtime, Ct));
    }
    [Fact]
    public async Task EarlierSessionSchemasCannotResumeOrSpend()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.SchemaVersion = 7;
        await Assert.ThrowsAsync<PlanningConflictException>(() => new TypedWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct));
        Assert.Empty(runtime.Calls);
    }
    [Fact]
    public async Task ClarificationValidatesBusinessAnswersWithoutResettingUsage()
    {
        var plan = PlannerFixture.Greeting(); plan.Questions.Add(new("tone", "Choose the greeting tone", new() { Type = "string", Enum = ["formal", "casual"] }));
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.Equal(1, state.ModelCalls);
        var planner = new TypedWorkflowPlanner();
        await Assert.ThrowsAsync<ArgumentException>(() => planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["tone"] = "unknown" } }, runtime, Ct));
        var next = await planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["tone"] = "formal" } }, runtime, Ct);
        Assert.Equal(state.ModelCalls, next.ModelCalls); Assert.Single(next.Answers); Assert.Null(next.GroundedPlan);
    }
}
