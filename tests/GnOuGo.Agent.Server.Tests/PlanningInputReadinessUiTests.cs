using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningInputReadinessUiTests : BunitContext
{
    [Fact]
    public void ReadinessIsEscapedUnverifiedAndNeverAcknowledgesRequirements()
    {
        IReadOnlyList<string>? reviewed = null;
        var state = new PlanningSession { Revision = 1, Status = PlanningStatus.FinalReview,
            Requirements = new() { Outcomes = [new("result", "Deliver the requested result")] },
            ValidationResults = [new("input_readiness:/tasks/observe", "unverified",
                "Runtime collection detection at /tasks/observe/inputs/<script>unsafe()</script>; 128 UTF-8 bytes, not a token estimate.", []),
                new("judge:reference:0", "inconclusive", "Rejected reference /taskPlan/root/outputs/<img src=x onerror=unsafe()> at /findings/0/evidence/1: location does not exist.", [])] };
        var cut = Render<PlannerStageDetails>(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state))
            .Add(c => c.ReviewEnabled, true).Add(c => c.Reviewed, ids => reviewed = ids));
        var section = cut.Find("details[aria-label='LLM input readiness']");
        Assert.Contains("Unverified", section.TextContent);
        Assert.Contains("/tasks/observe/inputs/<script>unsafe()</script>", section.TextContent);
        Assert.Contains("128 UTF-8 bytes", section.TextContent);
        Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll("img"));
        var judge = cut.Find("details[aria-label='Business judge observations']");
        Assert.Contains("/findings/0/evidence/1", judge.TextContent);
        Assert.Contains("<img src=x onerror=unsafe()>", judge.TextContent);
        Assert.Contains("inconclusive", judge.TextContent);
        Assert.NotNull(section.QuerySelector("summary"));
        Assert.Null(reviewed);
        Assert.False(cut.Find("input[type=checkbox]").HasAttribute("checked"));
        cut.Find("input[type=checkbox]").Change(true);
        Assert.Equal(["result"], reviewed);
        state.Revision++;
        cut.Render(p => p.Add(c => c.Session, PlanningEndpoints.ToDto(state)));
        Assert.False(cut.Find("input[type=checkbox]").HasAttribute("checked"));
        Assert.Contains("Unverified", cut.Find("details[aria-label='LLM input readiness']").TextContent);
    }
}
