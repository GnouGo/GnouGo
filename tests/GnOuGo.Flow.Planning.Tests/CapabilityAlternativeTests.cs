using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityAlternativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlternativeAnswerPreservesRequiredExecutionAndGrantsNoPermissions(bool custom)
    {
        var state = await ComposedOutcomeTests.State();
        var agent = ComposedOutcomeTests.Recorded("copilot").Catalog!.Capabilities.Single(c => c.StepType == "agent.run");
        state.Catalog!.Capabilities.Add(agent);
        state.Catalog.AllowedStepTypes.Add("agent.run");
        state.Catalog.Capabilities.Single(c => c.Id == "external").EffectKind = "execute";
        var alternate = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Catalog.Capabilities.Single(c => c.Id == "external"),
            PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
        alternate.Id = "alternate";
        alternate.Method = "sigma";
        state.Catalog.Capabilities.Add(alternate);
        state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with
        { Description = "Install dependencies and run tests", Execution = "execute" };
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var policy = JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy);
        var plan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var catalog = JsonSerializer.Serialize(state.Catalog, PlanningJsonContext.Default.PlanningCatalog);
        Assert.Contains("file.content", JsonSerializer.Serialize(agent, PlanningJsonContext.Default.PlanningCapability));
        var questions = new List<PlanningQuestion>
        {
            new("implementation", "This runner has file capabilities only. Choose a discovered execution alternative.",
                [new("external", "Use the available execution operation for the requested command work"),
                    new("alternate", "Use the alternative execution contract")], "external")
        };
        PlanningClarifications.Suspend(state, questions);
        state.PendingQuestions = questions;
        state.Status = PlanningStatus.Clarification;
        state = PlannerFixture.Clone(state);
        PlanningClarifications.Answer(state, new()
        {
            Answers = [custom
                ? new("implementation", Text: "Use the discovered external operation and preserve installation and tests")
                : new("implementation", "external")]
        });
        var recorded = Assert.Single(Assert.Single(state.AnswerHistory!).Answers);
        Assert.Equal("implementation", recorded.QuestionId);
        Assert.Equal(custom ? null : "external", recorded.AlternativeId);
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(policy, JsonSerializer.Serialize(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy));
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(catalog, JsonSerializer.Serialize(state.Catalog, PlanningJsonContext.Default.PlanningCatalog));
        Assert.Equal("execute", state.Requirements!.Outcomes[0].Execution);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Yaml);
        Assert.Null(state.ApprovedHash);
        Assert.Null(state.PendingQuestions);
    }
}
