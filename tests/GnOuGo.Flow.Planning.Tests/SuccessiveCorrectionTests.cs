using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SuccessiveCorrectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemainingLocalDefectKeepsItsScopeAndRepeatedCandidatesStop(bool repeated)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        state.Request.Options["automatic_correction"] = true;
        var plan = runtime.Proposal.Plan!;
        foreach (var id in new[] { "left", "right" }) plan.Root.Tasks.Add(new() { Id = id, Kind = "value", Objective = "Copy the declared result",
            Outputs = [new("value", new() { Kind = "output", Source = "perform", Port = "missing_" + id })] });
        runtime.Respond = (request, _) =>
        {
            var candidate = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
            if (runtime.Calls.Count >= 2) candidate.Root.Tasks[1].Outputs[0].Value.Port = "location";
            if (runtime.Calls.Count >= 3 && !repeated) candidate.Root.Tasks[2].Outputs[0].Value.Port = "location";
            return TestRuntime.Response(request, new() { Requirements = runtime.Calls.Count == 1 ? runtime.Proposal.Requirements : null, Plan = candidate });
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == (repeated ? PlanningStatus.Stopped : PlanningStatus.FinalReview), string.Join(';', state.Diagnostics) + state.Request.Options.ToJsonString());
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal("6", progress["version"]!.ToString());
        Assert.DoesNotContain(progress["attempts"]!.AsArray(), a => a!["action"]!.ToString() == "regenerate");
        Assert.All(runtime.Calls.Skip(1), call => Assert.True(PlanningRepairPatch.Issued(call.StructuredOutputSchema!.AsObject())));
        Assert.Null(state.ApprovedHash);
        var recovered = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(3, recovered.ModelCalls);
    }
}
