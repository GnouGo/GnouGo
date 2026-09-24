using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Agent.Server.Tests;
public sealed class PlannerClarificationUiTests : BunitContext
{
    [Fact]
    public void TypedAnswersRequireExplicitSubmitAndBusinessTextIsEncoded()
    {
        JsonObject? answer = null;
        var cut = Render<PlannerClarificationCard>(p => p.Add(c => c.Questions,
            [new PlanningQuestionDto("tone", "Which <script>unsafe()</script> tone?", new() { ["type"] = "string" })]).Add(c => c.Answer, a => answer = a));
        Assert.Null(answer); Assert.Empty(cut.FindAll("script"));
        cut.Find("input").Input("brief"); Assert.Null(answer);
        cut.Find("form").Submit(); Assert.Equal("brief", answer!["tone"]!.ToString());
    }
    [Fact]
    public void MalformedTypedAnswerCannotSubmitAndCancelIsSeparate()
    {
        JsonObject? answer = null; var cancelled = false;
        var cut = Render<PlannerClarificationCard>(p => p.Add(c => c.Questions,
            [new PlanningQuestionDto("amount", "How much?", new() { ["type"] = "number" })]).Add(c => c.Answer, a => answer = a).Add(c => c.Cancel, () => cancelled = true));
        cut.Find("input").Input("no number"); cut.Find("form").Submit(); Assert.Null(answer); Assert.Single(cut.FindAll("[role=alert]"));
        cut.FindAll("button")[1].Click(); Assert.True(cancelled); Assert.Null(answer);
    }
    [Fact]
    public void StoppedPlanningShowsRevisionScopeAndDiscoveryLimitations()
    {
        var state = new PlanningSession { Status = PlanningStatus.Stopped, RevisionScope = ["publish"], Discovery = new() { Limitations = ["Additional sources were not inspected"] } };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.Contains("publish", cut.Markup); Assert.Contains("Additional sources", cut.Markup); Assert.Empty(cut.FindAll("button"));
    }
}
