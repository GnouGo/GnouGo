using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContractMaterializationGuidanceTests
{
    [Theory]
    [InlineData("construction_discovery", "description:operation", true)]
    [InlineData("construction_discovery", "source-description:provider", false)]
    [InlineData("construction_implementation", "description:operation", false)]
    public async Task FreshGuideRequiresAnInspectedOperationDescriptionAndPreservesAuthority(string purpose, string address, bool expected)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate;
        state.Construction!.Evidence = new JsonObject { [address] = new JsonObject { ["text"] = "Owned description" } };
        const string prompt = "Preserve the original obligation.\n{\"original\":\"Exact text\"}";
        var schema = PlanningSchemas.Object(("answer", PlanningSchemas.Enum("observed")));
        var request = PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema);
        Assert.Equal(expected, request.Prompt.Contains("retrieve its exact owned contract before drafting", StringComparison.Ordinal));
        Assert.EndsWith(prompt[prompt.LastIndexOf('\n')..], request.Prompt, StringComparison.Ordinal);
        Assert.Equal(request.ClientRequestId, PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema).ClientRequestId);
        Assert.Null(state.PendingCall);
        Assert.Same(candidate, state.Construction.Candidate);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["answer"] = "observed" }, request.StructuredOutputSchema!));
    }

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
