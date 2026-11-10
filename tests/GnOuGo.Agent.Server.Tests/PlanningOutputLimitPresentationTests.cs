using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningOutputLimitPresentationTests : BunitContext
{
    [Theory]
    [InlineData("MODEL_OUTPUT_LIMIT", "blocked", true)]
    [InlineData("CONTRACT_UNSATISFIED, MODEL_OUTPUT_LIMIT", "blocked", true)]
    [InlineData("MODEL_INPUT_LIMIT", "blocked", false)]
    [InlineData("MODEL_OUTPUT_LIMIT", "succeeded", false)]
    public void TimelineDistinguishesProviderCutoffFromAdvisoryThresholds(string reason, string outcome, bool cutoff)
    {
        var state = new PlanningSession { Request = new() { SessionId = "presentation", Options = new()
            { ["agent_host_policy"] = new JsonObject { ["thresholdMode"] = "diagnostic" } } },
            InspectionEvents = [new(1, DateTimeOffset.UtcNow, "construction_draft", outcome, reason,
                "presentation:1:hash", "block_1", "step_1", 0, "Prepare a table")] };
        var dto = PlanningEndpoints.ToDto(state);
        var before = state.InspectionEvents.ToArray();
        var cut = Render<PlanningDiagnostics>(p => p.Add(c => c.Session, dto));
        Assert.Contains("Incomplete model responses still stop planning", cut.Markup);
        Assert.Equal(cutoff, cut.FindAll(".planning-output-limit").Count == 1);
        if (cutoff)
        {
            Assert.Contains("provider stopped before completing its response", cut.Markup);
            Assert.Contains("not an advisory threshold", cut.Markup);
            Assert.DoesNotContain("Resume", cut.Markup);
        }
        Assert.Equal(before, state.InspectionEvents);
    }
}
