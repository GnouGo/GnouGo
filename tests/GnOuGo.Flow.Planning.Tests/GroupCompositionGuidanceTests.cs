using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class GroupCompositionGuidanceTests
{
    [Theory]
    [InlineData("Select an available local construction.")]
    [InlineData("Original business text:\n{\"kind\":\"unrelated\"}\nSelect a permitted construction.")]
    public void GuidanceKeepsOriginalContextAndRequiresExplicitExistingGroupSelection(string instruction)
    {
        var context = new JsonObject { ["original"] = "Preserve every ordered item, including duplicates.",
            ["group"] = new JsonObject { ["id"] = "existing", ["closed"] = true } }.ToJsonString();
        var result = PlanningPromptPresentation.PresentGroupCompositionGuidance(instruction + "\n" + context);
        Assert.StartsWith(instruction, result, StringComparison.Ordinal);
        Assert.EndsWith("\n" + context, result, StringComparison.Ordinal);
        Assert.Contains("both the control kind and that existing group's groupId", result, StringComparison.Ordinal);
        Assert.Contains("neither declares a new group nor executes it", result, StringComparison.Ordinal);
        Assert.DoesNotContain("groupId:existing", result, StringComparison.Ordinal);
    }
}
