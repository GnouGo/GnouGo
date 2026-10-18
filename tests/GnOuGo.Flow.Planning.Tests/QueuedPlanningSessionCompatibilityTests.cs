using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class QueuedPlanningSessionCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalSerializedZeroProgressSessionKeepsItsCapturedOrAbsentProfiles(bool captured)
    {
        var state = PlannerFixture.Session();
        if (captured)
        {
            state.Request.Options["planning_input_review_version"] = 1;
            state.Request.Options["business_judge_review_version"] = 1;
        }
        state = PlannerFixture.Clone(state); // A queued session persisted by the previous binary.
        Assert.Equal(0, state.Revision);
        Assert.Equal(0, state.ModelCalls);
        Assert.Null(state.Plan);
        Assert.Null(state.PendingCall);
        var runtime = new TestRuntime();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Null(state.Request.Options["planning_diagnostics_version"]);
        Assert.Equal(captured ? "1" : null, state.Request.Options["planning_input_review_version"]?.ToString());
        Assert.Equal(captured ? "1" : null, state.Request.Options["business_judge_review_version"]?.ToString());
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task FreshDirectSessionCapturesNewProfilesBeforeItsCoordinatorClone()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime(), PlannerFixture.Session());
        Assert.Equal("1", state.Request.Options["planning_diagnostics_version"]!.ToString());
        Assert.Equal("2", state.Request.Options["planning_input_review_version"]!.ToString());
        Assert.Equal("2", state.Request.Options["business_judge_review_version"]!.ToString());
        Assert.DoesNotContain("isNewlyConstructed", JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task AlreadyCapturedProfilesRemainStableOnAnUnserializedSession()
    {
        var state = PlannerFixture.Session();
        state.Request.Options["compilation_profile"] = "compact-bindings-v10";
        state.Request.Options["planning_input_review_version"] = 1;
        state.Request.Options["business_judge_review_version"] = 1;
        state = await PlannerFixture.RunAsync(new TestRuntime(), state);
        Assert.Equal("compact-bindings-v10", state.Request.Options["compilation_profile"]!.ToString());
        Assert.Equal("1", state.Request.Options["planning_input_review_version"]!.ToString());
        Assert.Equal("1", state.Request.Options["business_judge_review_version"]!.ToString());
    }
}
