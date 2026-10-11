using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Agent.Server.Tests;
public sealed class PlannerChoiceUiTests : BunitContext
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("covered", "covered")]
    public void ChatRadioFieldsRequireAnExplicitDefaultOrSelection(string? suppliedDefault, string expected)
    {
        // Exercise the existing chat form's defaulting path without starting a conversation.
        var page = new GnOuGo.Agent.Server.Components.Pages.ChatPage();
        var type = page.GetType();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fieldType = type.GetNestedType("HumanInputFieldModel", System.Reflection.BindingFlags.NonPublic)!;
        var field = Activator.CreateInstance(fieldType)!;
        fieldType.GetProperty("Name")!.SetValue(field, "requirement:visit");
        fieldType.GetProperty("Type")!.SetValue(field, "radio");
        fieldType.GetProperty("Options")!.SetValue(field, new List<string> { "covered", "missing", "uncertain" });
        fieldType.GetProperty("Default")!.SetValue(field, suppliedDefault);
        var fields = Array.CreateInstance(fieldType, 1); fields.SetValue(field, 0);
        var seed = type.GetMethod("SeedHumanInputFieldDefaults", flags)!;
        var get = type.GetMethod("GetFieldValue", flags)!;
        seed.Invoke(page, [fields]);
        Assert.Equal(expected, get.Invoke(page, ["requirement:visit"]));
        fieldType.GetProperty("Default")!.SetValue(field, null);
        seed.Invoke(page, [fields]);
        Assert.Equal("", get.Invoke(page, ["requirement:visit"]));
    }

    [Fact]
    public void RequirementReviewIsExplicitAccessibleAndClearedForAnotherArtifact()
    {
        IReadOnlyList<string>? reviewed = null;
        var state = new PlanningSession { IntentVersion = 2, Revision = 1, Status = PlanningStatus.FinalReview,
            Requirements = new() { Outcomes = [new("each", "For each resource, visit then extract."), new("complete", "Consume complete observations.")] } };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state))
            .Add(c => c.ReviewEnabled, true).Add(c => c.Reviewed, ids => reviewed = ids));
        Assert.Null(reviewed); Assert.All(cut.FindAll("input"), e => Assert.False(e.HasAttribute("checked")));
        Assert.Equal(2, cut.FindAll("label input[type=checkbox]").Count);
        cut.FindAll("input")[0].Change(true); Assert.Equal(["each"], reviewed);
        cut.FindAll("input")[1].Change(true); Assert.Equal(["each", "complete"], reviewed);
        cut.FindAll("input")[0].Change(false); Assert.Equal(["complete"], reviewed);
        state.Revision++;
        cut.Render(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.All(cut.FindAll("input"), e => Assert.False(e.HasAttribute("checked")));
        cut.Render(p => p.Add(c => c.Busy, true)); Assert.All(cut.FindAll("input"), e => Assert.True(e.HasAttribute("disabled")));
        cut.Render(p => p.Add(c => c.ReviewEnabled, false)); Assert.Empty(cut.FindAll("input"));
    }

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
    public void HistoricalChecksRemainReadableAndSeparateFromExecutionApproval()
    {
        var state = new PlanningSession { Status = PlanningStatus.FinalReview, ValidationResults =
            [new("outcome:publish", "supported", "Publish <script>unsafe()</script> via task send. External success has not been observed.", [])] };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.Contains("Historical outcome checks", cut.Markup); Assert.Contains("send", cut.Markup);
        Assert.Contains("success has not been observed", cut.Markup); Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll("button")); Assert.NotNull(cut.Find("details[open] summary"));
    }

    [Fact]
    public void BusinessRequirementsAreReviewedWithoutTechnicalProofAnnotations()
    {
        var state = new PlanningSession { IntentVersion = 2, Requirements = new() { Summary = "Make a report", Inputs = [], Outputs = [new() { Name = "report", Type = new() { Kind = "string" } }],
            Outcomes = [new("report", "Write <script>unsafe()</script> rows")] } };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.Contains("Business requirements", cut.Markup); Assert.Contains("Caller inputs: none", cut.Markup);
        Assert.Contains("Business outputs: report: string", cut.Markup);
        Assert.Contains("does not prove business completeness", cut.Markup); Assert.Empty(cut.FindAll("script"));
        Assert.DoesNotContain("Historical outcome checks", cut.Markup); Assert.Empty(cut.FindAll("button"));
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
