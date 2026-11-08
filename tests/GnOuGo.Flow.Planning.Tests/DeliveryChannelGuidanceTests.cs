using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DeliveryChannelGuidanceTests
{
    [Fact]
    public async Task GlobalReviewAddsDeliveryGuidanceWithoutChangingOriginalsOrRemovingDefectChoices()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var before = System.Text.Json.JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: true);
        Assert.Contains("delivery channel required by the original obligation", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("needs_change", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.True(context.ContainsKey("declaredPublicOutputs"));
        Assert.True(context.ContainsKey("publicOutputs"));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var (_, local) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        Assert.DoesNotContain("delivery channel required by the original obligation", local.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Save the feedback in the requested file.")]
    [InlineData("Return the result to the caller and also save the file.")]
    public void GuidancePreservesTheWholeOriginalAndDoesNotSetABusinessVerdict(string original)
    {
        var context = new JsonObject { ["original"] = original, ["declaredPublicOutputs"] = new JsonArray() };
        var prompt = "Assess actual behavior.\n" + context.ToJsonString();
        var guided = PlanningPromptPresentation.PresentDeliveryChannelGuidance(prompt);
        Assert.EndsWith("\n" + context.ToJsonString(), guided, StringComparison.Ordinal);
        Assert.Contains("A specifically requested caller return remains mandatory", guided, StringComparison.Ordinal);
        Assert.Contains("Assess each requested channel and its actual provenance independently", guided, StringComparison.Ordinal);
        Assert.DoesNotContain("Return ok", guided, StringComparison.Ordinal);
        Assert.DoesNotContain("Code", guided, StringComparison.Ordinal);
    }
}
