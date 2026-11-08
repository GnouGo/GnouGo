using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContractMaterializationGuidanceTests
{
    [Theory]
    [InlineData("Choose suitable operations for the original obligation.")]
    [InlineData("Do not execute any business action during planning.")]
    public void GuidancePreservesOriginalContextAndSeparatesSelectionFromExecution(string instruction)
    {
        var context = new JsonObject { ["original"] = "Keep the user's actual obligation.", ["operationIds"] = new JsonArray("owned-operation") }.ToJsonString();
        var prompt = instruction + "\n" + context;
        var presented = PlanningPromptPresentation.PresentContractMaterializationGuidance(prompt);
        Assert.StartsWith(instruction, presented, StringComparison.Ordinal);
        Assert.EndsWith("\n" + context, presented, StringComparison.Ordinal);
        Assert.Contains("retrieve its exact owned contract before drafting", presented, StringComparison.Ordinal);
        Assert.Contains("does not assert known parameter values, successful execution or business completion", presented, StringComparison.Ordinal);
        Assert.Contains("against the actual contract", presented, StringComparison.Ordinal);
    }
}
