using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlannerScopeRevisionUiTests : BunitContext
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void SharedCardRequiresExplicitConsentAndEncodesBusinessText(int button, bool expected)
    {
        bool? answer = null;
        var cut = Render<PlannerScopeRevisionCard>(p => p.Add(c => c.Question, "Accept <script>unsafe()</script> as the revised outcome?")
            .Add(c => c.Answer, value => answer = value));
        Assert.Null(answer); Assert.Empty(cut.FindAll("script"));
        cut.FindAll("button")[button].Click(); Assert.Equal(expected, answer);
    }

    [Fact]
    public void SharedCardCancelsWithoutAcceptingARevision()
    {
        var cancelled = false; bool? answer = null;
        var cut = Render<PlannerScopeRevisionCard>(p => p.Add(c => c.Question, "Proposed outcome")
            .Add(c => c.Answer, value => answer = value).Add(c => c.Cancel, () => cancelled = true));
        cut.FindAll("button")[2].Click(); Assert.True(cancelled); Assert.Null(answer);
    }

    [Fact]
    public void AutoStopShowsTheUnsupportedOutcomeAndProposalWithoutAConsentButton()
    {
        var state = new PlanningSession { Status = PlanningStatus.Stopped, PendingRepair = new()
        { Candidate = new() { Summary = "Original requirements" }, ActionIds = ["publish"],
            Questions = [new("accept_scope_revision", "Per-item publication is unsupported. Accept a summarized body?", new() { Type = "boolean" })] } };
        var dto = PlanningEndpoints.ToDto(state);
        var cut = Render<PlannerRepairDetails>(p => p.Add(c => c.Session, dto));
        Assert.Contains("Per-item publication is unsupported", cut.Markup); Assert.Contains("summarized body", cut.Markup);
        Assert.Contains("not applied", cut.Markup); Assert.Empty(cut.FindAll("button"));
    }
}
