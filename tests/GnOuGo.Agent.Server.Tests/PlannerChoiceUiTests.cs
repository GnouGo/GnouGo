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
}
