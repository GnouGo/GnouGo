using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompletionEligibilityTests
{
    [Theory]
    [InlineData("unused-required-group")]
    [InlineData("missing-call-input")]
    [InlineData("disconnected-public-result")]
    public async Task EstablishedTechnicalObstaclesCannotOfferCompletion(string obstacle)
    {
        var (state, runtime) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        if (obstacle != "unused-required-group") BusinessSelectionCoverageContextTests.AddCall(state, conditional: false);
        if (obstacle == "missing-call-input") state.Plan!.Root.Tasks.Last().Inputs.Clear();
        if (obstacle == "disconnected-public-result")
        {
            state.Requirements!.Outputs = [new() { Name = "result", Required = true, Type = new() { Kind = "number" } }];
            state.Requirements.Outcomes[1] = state.Requirements.Outcomes[1] with
                { Commitments = [new("remote_calculation") { Result = new("result", "result") }] };
            state.Plan!.Root.Outputs = [new("result", new() { Kind = "number", Number = 9 })];
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var calls = runtime.Calls.Count;
        Assert.False(OffersCompletion(state));
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.SelectCompletion(state, ["later"]));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(calls, runtime.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReachableInvocationAllowsReviewWithoutClaimingExecution(bool conditional)
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        BusinessSelectionCoverageContextTests.AddCall(state, conditional);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.True(OffersCompletion(state), string.Join("; ", PlanningCompletionValidation.Assess(state, state.Plan!).Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task UnusedOptionalGroupDoesNotBecomeARequiredInvocation()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        state.Requirements!.Outcomes[1] = state.Requirements.Outcomes[1] with { Commitments = [] };
        Assert.True(OffersCompletion(state), string.Join("; ", PlanningCompletionValidation.Assess(state, state.Plan!).Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Empty(state.Plan!.Root.Outputs);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task LegitimatelyEmptyIterationRemainsUnverifiedRatherThanTechnicallyForbidden()
    {
        var (state, _) = await BusinessSelectionCoverageContextTests.PreparedGroup();
        var values = new TaskInput { Name = "values", Required = true,
            Type = new() { Kind = "array", Items = new() { Kind = "number" }, MaxItems = 0 } };
        state.Plan!.Inputs.Add(values);
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Plan.Root.Tasks.Add(new() { Id = "each", Kind = "foreach", Objective = "Use each supplied value, including an empty collection",
            Items = new() { Kind = "input", Source = "values" }, Body = new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "prepared",
                Objective = "Use the prepared contribution for this value", Inputs = [new("x", new() { Kind = "item" })] }] } });
        var assessment = PlanningCompletionValidation.Assess(state, state.Plan);
        Assert.Empty(assessment.Diagnostics);
        Assert.Contains(assessment.Coverage, result => result.Outcome == "unverified");
        Assert.True(OffersCompletion(state));
        Assert.Null(state.ApprovedHash);
    }

    internal static bool OffersCompletion(PlanningSession state) =>
        PlanningCompactConstruction.SelectionSchema(state).ToJsonString().Contains("\"completionRequirementIds\"", StringComparison.Ordinal);
}
