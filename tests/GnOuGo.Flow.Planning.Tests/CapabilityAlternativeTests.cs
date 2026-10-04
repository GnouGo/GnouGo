using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityAlternativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedRunnerUsesExistingClarificationWithoutLosingIntentOrGrantingPermissions(bool custom)
    {
        var state = await ComposedOutcomeTests.State();
        var agent = ComposedOutcomeTests.Recorded("copilot").Catalog!.Capabilities.Single(c => c.StepType == "agent.run");
        state.Catalog!.Capabilities.Add(agent); state.Catalog.AllowedStepTypes.Add("agent.run");
        state.Catalog.Capabilities.Single(c => c.Id == "external").EffectKind = "execute";
        var alternate = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Catalog.Capabilities.Single(c => c.Id == "external"), PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
        alternate.Id = "alternate"; alternate.Method = "sigma"; state.Catalog.Capabilities.Add(alternate);
        var plan = state.Plan; state.Plan = null; state.OutcomeBindings = null;
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Description = "Install dependencies and run tests", Execution = "execute" };
        state.Request.Mode = "auto"; state.Request.Generation.MaxInputTokensPerRequest = 96000;
        var policy = JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy);
        var runtime = new TestRuntime { Proposal = new() { Clarifications = [new("implementation", "This runner has file capabilities only. Choose a discovered execution alternative.",
            [new("external", "Use the available execution operation; keeps the requested command work"), new("alternate", "Use the alternative executor; different resolved execution contract")], "external")] } };
        var invalid = PlanningContractValidation.ValidateSchema(new PlanningPrompt(state).Request().StructuredOutputSchema!.AsObject(), strict: true); Assert.True(invalid.Count == 0, string.Join("; ", invalid));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.Clarification, string.Join("; ", state.Diagnostics.Select(d => d.Code + " " + d.Message))); Assert.Null(state.ApprovedHash); Assert.Equal(1, state.ModelCalls);
        Assert.Contains("file.content", runtime.Calls.Single().Prompt); Assert.Contains("external", runtime.Calls.Single().Prompt);
        Assert.Contains("compatible alternatives", runtime.Calls.Single().Prompt);
        var expected = state.Requirements;
        expected!.Outcomes[0] = expected.Outcomes[0] with { Operation = "external" };
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [custom ? new("implementation", Text: "Use the discovered external operation and preserve installation and tests") : new("implementation", "external")] }, runtime, PlannerFixture.Ct);
        Assert.Equal(1, state.ModelCalls); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(policy, JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy));
        runtime.Proposal = new() { Plan = plan, Requirements = expected, OutcomeBindings = [new("work", ["perform"], ["report"])] };
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + " " + d.Message))); Assert.Null(state.ApprovedHash); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Requirements!.Outcomes.Single().Execution);
        PlanningArtifactApproval.Verify(state);
    }
}
