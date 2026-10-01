using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Agent.Server.Tests;
public sealed class PlannerChoiceUiTests : BunitContext
{
    [Fact]
    public void RecommendationRequiresExplicitSubmitAndBusinessTextIsEncoded()
    {
        JsonObject? selections = null;
        var cut = Render<PlannerChoiceCard>(p => p.Add(c => c.Choices,
            [new PlanningChoiceDto("tone", "Which <script>unsafe()</script> tone?", [new("brief", "Brief", null), new("full", "Full", null)], "brief", null)])
            .Add(c => c.Select, a => selections = a));
        Assert.Null(selections); Assert.Empty(cut.FindAll("script")); Assert.Contains("Recommended", cut.Markup);
        cut.Find("select").Change("full"); Assert.Null(selections);
        cut.Find("form").Submit(); Assert.Equal("full", selections!["tone"]!.ToString());
    }
    [Fact]
    public void CancellationDoesNotSelectTheRecommendation()
    {
        JsonObject? selections = null; var cancelled = false;
        var cut = Render<PlannerChoiceCard>(p => p.Add(c => c.Choices,
            [new PlanningChoiceDto("tone", "Which tone?", [new("brief", "Brief", null), new("full", "Full", null)], "brief", null)])
            .Add(c => c.Select, a => selections = a).Add(c => c.Cancel, () => cancelled = true));
        cut.FindAll("button")[1].Click(); Assert.True(cancelled); Assert.Null(selections);
    }
    [Fact]
    public void StoppedPlanningShowsRevisionScopeAndDiscoveryLimitations()
    {
        var state = new PlanningSession { Status = PlanningStatus.Stopped, RevisionScope = ["publish"], Discovery = new() { Limitations = ["Additional sources were not inspected"] } };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.Contains("publish", cut.Markup); Assert.Contains("Additional sources", cut.Markup); Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void OutcomeSupportIsReadableEncodedAndSeparateFromExecutionApproval()
    {
        var state = new PlanningSession { Status = PlanningStatus.FinalReview, ValidationResults =
            [new("outcome:publish", "supported", "Publish <script>unsafe()</script> via task send. External success has not been observed.", [])] };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.Contains("Outcome implementation", cut.Markup); Assert.Contains("send", cut.Markup);
        Assert.Contains("success has not been observed", cut.Markup); Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll("button")); Assert.NotNull(cut.Find("details[open] summary"));
    }

    private static PlanningQuestionDto Question(string id = "interface") => new(id, "Which interface?",
        [new("compact", "Reference and instructions", null), new("coordinates", "Explicit coordinates", null)], "compact");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyQuestionSubmitsRecommendationOrCustomTextOnlyExplicitly(bool custom)
    {
        IReadOnlyList<PlanningAnswerDto>? answers = null;
        var cut = Render<PlannerChoiceCard>(p => p.Add(c => c.Questions, [Question()]).Add(c => c.Answer, a => answers = a));
        Assert.Null(answers); Assert.Contains("Recommended", cut.Markup);
        Assert.Equal(cut.Find("select").Id, cut.Find("label").GetAttribute("for"));
        if (custom)
        {
            cut.Find("select").Change(""); cut.Find("form").Submit();
            Assert.Null(answers); Assert.Contains("Enter an answer", cut.Find("[role=alert]").TextContent);
            cut.Find("textarea").Input("Only a reference; derive the rest <script>unsafe()</script>");
        }
        Assert.Null(answers); cut.Find("form").Submit();
        var answer = Assert.Single(answers!); Assert.Equal("interface", answer.QuestionId);
        if (custom) { Assert.Null(answer.AlternativeId); Assert.Contains("derive the rest", answer.Text); }
        else { Assert.Equal("compact", answer.AlternativeId); Assert.Null(answer.Text); }
        Assert.Empty(cut.FindAll("script"));
    }

    [Fact]
    public void TextOnlyQuestionIsAccessibleAndRevisionClearsStaleAnswers()
    {
        IReadOnlyList<PlanningAnswerDto>? answers = null;
        var cut = Render<PlannerChoiceCard>(p => p.Add(c => c.Questions, [new("fact", "Which resource?", [], null)])
            .Add(c => c.DecisionKey, "session:1").Add(c => c.Answer, a => answers = a));
        Assert.Empty(cut.FindAll("select")); Assert.Equal(cut.Find("textarea").Id, cut.Find("label").GetAttribute("for"));
        cut.Find("textarea").Input("old input");
        cut.Render(p => p.Add(c => c.DecisionKey, "session:2")); cut.Find("form").Submit(); Assert.Null(answers);
        cut.Find("textarea").Input("new input"); cut.Find("form").Submit(); Assert.Equal("new input", Assert.Single(answers!).Text);
    }

    [Fact]
    public void CustomLiteralChoiceUsesRevisionAnswerRatherThanUncheckedSelection()
    {
        JsonObject? selections = null; IReadOnlyList<PlanningAnswerDto>? answers = null;
        var cut = Render<PlannerChoiceCard>(p => p.Add(c => c.Choices,
                [new("tone", "Which tone?", [new("brief", "Brief", null), new("full", "Full", null)], "brief", null)])
            .Add(c => c.Select, a => selections = a).Add(c => c.Answer, a => answers = a));
        cut.Find("select").Change(""); cut.Find("textarea").Input("Use a diagram"); cut.Find("form").Submit();
        Assert.Null(selections); Assert.Equal("Use a diagram", Assert.Single(answers!).Text);
    }

    [Fact]
    public void BusyAndCancellationCannotSubmitAnswers()
    {
        IReadOnlyList<PlanningAnswerDto>? answers = null; var cancelled = false;
        var cut = Render<PlannerChoiceCard>(p => p.Add(c => c.Questions, [Question()]).Add(c => c.Answer, a => answers = a)
            .Add(c => c.Busy, true).Add(c => c.Cancel, () => cancelled = true));
        Assert.All(cut.FindAll("button, select"), c => Assert.True(c.HasAttribute("disabled")));
        cut.Find("form").Submit(); Assert.Null(answers);
        cut.Render(p => p.Add(c => c.Busy, false)); cut.FindAll("button")[1].Click(); Assert.True(cancelled); Assert.Null(answers);
    }
}
