using Bunit;
using GnOuGo.Agent.Server.Components.Planning;

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
}
