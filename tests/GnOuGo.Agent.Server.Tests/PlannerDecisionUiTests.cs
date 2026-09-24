using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlannerDecisionUiTests : BunitContext
{
    private static PlanningDecisionDto Decision() => new("decision", "How much detail?", "Both formats satisfy the report requirements.", "binding", "/actions/report", ["report"],
        [new("brief", "Brief", "Easier to scan", true), new("full", "Full", "Retains all detail", false)], true);
    [Fact]
    public void PreferredIsVisibleButRequiresExplicitSubmitAndAlternativesWork()
    {
        PlanningDecisionAnswerDto? answer = null;
        var cut = Render<PlannerDecisionCard>(p => p.Add(c => c.Decision, Decision()).Add(c => c.Answer, a => answer = a));
        Assert.Null(answer); Assert.Contains("Preferred", cut.Find(".planner-option--preferred").TextContent);
        Assert.Contains("Retains all detail", cut.Markup);
        cut.FindAll("input")[1].Change(true);
        cut.FindAll("button")[0].Click();
        Assert.Equal("full", answer!.OptionId); Assert.Null(answer.Text);
    }
    [Fact]
    public void CustomAnswerAndCancelAreSeparateActions()
    {
        PlanningDecisionAnswerDto? answer = null; var cancelled = false;
        var cut = Render<PlannerDecisionCard>(p => p.Add(c => c.Decision, Decision()).Add(c => c.Answer, a => answer = a).Add(c => c.Cancel, () => cancelled = true));
        cut.Find("textarea").Input("  Executive summary  "); cut.FindAll("button")[0].Click();
        Assert.Equal("Executive summary", answer!.Text); Assert.Null(answer.OptionId);
        cut.FindAll("button")[1].Click(); Assert.True(cancelled);
    }
    [Fact]
    public void HistoryShowsAutomaticReasonAndBusinessScope()
    {
        var cut = Render<PlannerDecisionHistory>(p => p.Add(c => c.Decisions, [new(Decision(), new("decision", "brief"), "auto", "Easier to scan", DateTimeOffset.UtcNow)]));
        Assert.Contains("Automatic", cut.Markup); Assert.Contains("Easier to scan", cut.Markup); Assert.Contains("/actions/report", cut.Markup);
    }
}
