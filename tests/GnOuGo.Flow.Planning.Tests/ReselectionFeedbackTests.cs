using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ReselectionFeedbackTests
{
    [Theory]
    [InlineData("foreach", "CONSTRUCTION_TENTATIVE_SELECTION", true)]
    [InlineData("sequence", "CONSTRUCTION_TENTATIVE_SELECTION", true)]
    [InlineData("foreach", "CONSTRUCTION_STEP_INVALID", false)]
    public void ReselectedControlUsesItsDeclaredFieldsWhileKeepingTheOriginalRejection(string nextKind, string diagnostic, bool sameTentative)
    {
        var (state, _) = FocusedDiscoveryTests.Ready(new("source", 1));
        var compact = state.Construction!.Compact!;
        var selected = compact.CurrentStep!;
        selected.Kind = "foreach";
        selected.DiscoveryComplete = true;
        const string reason = "The selected task does not offer the required bound.";
        state.Diagnostics = [new(diagnostic, "/step", reason)];
        PlanningCompactConstruction.Reselect(state, reason);
        Assert.Equal(reason, PlanningCompactConstruction.ImplementationContext(state)["reselection"]?["reason"]?.ToString());
        selected.Id = "replacement";
        selected.Kind = nextKind;
        compact.CurrentStep = selected;
        var restored = PlannerFixture.Clone(state);
        var draft = PlanningConstruction.DraftRequest(restored, repair: false);
        var context = JsonNode.Parse(draft.Prompt[(draft.Prompt.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        var feedback = context["reselection"]!;
        Assert.Equal(reason, restored.Construction!.Compact!.RejectedSteps[0]["reason"]!.ToString());
        Assert.Equal("rejected:0", feedback["evidence"]!.ToString());
        if (sameTentative)
        {
            Assert.Null(feedback["reason"]);
            var contract = feedback["currentControl"]!;
            Assert.Equal(nextKind, contract["kind"]!.ToString());
            var nested = feedback["nestedControls"]!.AsObject();
            Assert.True(JsonNode.DeepEquals(contract, nested[nextKind]));
            var loop = nested["foreach"]!;
            var issued = PlanningModelCalls.PrepareRequest(restored, PlanningConstruction.DraftPurpose, draft.Prompt, draft.StructuredOutputSchema!.AsObject());
            var shape = Assert.Single(PlanningSchemaReferences.Walk(issued.StructuredOutputSchema!.AsObject(), "", 0),
                entry => entry.Schema["properties"]?["kind"]?["enum"] is JsonArray { Count: 1 } kinds && kinds[0]?.ToString() == "foreach");
            Assert.Contains("maxItems", loop["required"]!.AsArray().Select(field => field!.ToString()));
            Assert.True(JsonNode.DeepEquals(shape.Schema["required"], loop["required"]));
            foreach (var constraint in new[] { "type", "minimum", "maximum" })
                Assert.True(JsonNode.DeepEquals(shape.Schema["properties"]!["maxItems"]![constraint], loop["integerFields"]!["maxItems"]![constraint]));
            Assert.Equal(TaskPlanCompiler.MaximumIterationItems, loop["integerFields"]!["maxItems"]!["maximum"]!.GetValue<int>());
        }
        else
        {
            Assert.Equal(reason, feedback["reason"]!.ToString());
            Assert.Null(feedback["currentControl"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedChoiceRemainsVisibleAtDiscoveryAndDraftAfterReload(bool diagnostic)
    {
        var (state, _) = FocusedDiscoveryTests.Ready(new("renamed-source", 48));
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var step = state.Construction!.Compact!.CurrentStep!; step.Kind = "value";
        const string reason = "Copying the prepared record does not publish it through an authorized communication operation.";
        state.Diagnostics = [new("CONSTRUCTION_RESELECTION", "/step", reason)];
        PlanningCompactConstruction.Reselect(state, reason);
        Assert.Equal(reason, PlanningCompactConstruction.ImplementationContext(state)["reselection"]?["reason"]?.ToString());
        var selected = JsonSerializer.Deserialize(JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep), PlanningJsonContext.Default.PlanningConstructionStep)!;
        selected.Id = "next-step"; selected.Kind = null; selected.DiscoveryComplete = false;
        state.Construction.Compact.CurrentStep = selected;
        state = PlannerFixture.Clone(state);
        Assert.Equal(reason, PlanningCompactConstruction.DiscoveryContext(state)["reselection"]?["reason"]?.ToString());
        Assert.Equal("rejected:0", PlanningCompactConstruction.DiscoveryContext(state)["reselection"]?["evidence"]?.ToString());
        selected = state.Construction!.Compact!.CurrentStep!; selected.Kind = "value"; selected.DiscoveryComplete = true;
        Assert.Equal(reason, PlanningCompactConstruction.Context(state)["reselection"]?["reason"]?.ToString());
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("block")]
    [InlineData("scope")]
    [InlineData("requirements")]
    public void UnrelatedRejectionCannotBecomeCurrentFeedback(string mismatch)
    {
        var (state, _) = FocusedDiscoveryTests.Ready(new("unrelated-source", 1));
        var step = state.Construction!.Compact!.CurrentStep!;
        state.Diagnostics = [new("CONSTRUCTION_RESELECTION", "/step", "Previous finding")];
        PlanningCompactConstruction.Reselect(state, "Previous finding");
        state.Construction.Compact.CurrentStep = step;
        switch (mismatch)
        {
            case "baseline": step.BaseHash = "different-baseline"; break;
            case "block": step.BlockId = "different-block"; break;
            case "scope": step.Scope = "/different-scope"; break;
            default: step.RequirementIds = ["different-requirement"]; break;
        }
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["reselection"]);
    }
}
