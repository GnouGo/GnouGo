using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class GroupCompositionGuidanceTests
{
    [Theory]
    [InlineData("construction_implementation", false)]
    [InlineData("construction_discovery", false)]
    [InlineData("construction_implementation", true)]
    [InlineData("construction_discovery", true)]
    public async Task FreshSelectionOffersGuidanceOnlyForAnExistingClosedGroupWithoutSelectingIt(string purpose, bool open)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Plan!.Groups = [new() { Id = "reusable", Body = new() { Tasks = [new()
        {
            Id = "greet", Kind = "value", Objective = "Return the original greeting.",
            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })]
        }], Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })] } }];
        state.Construction!.OpenScopes = open ? ["/root", "/groups/reusable/body"] : ["/root"];
        const string prompt = "Preserve original obligations.\n{\"original\":\"Exact text with spaces\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""")!.AsObject();
        var original = schema.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema);
        Assert.Equal(!open, request.Prompt.Contains("both the control kind and that existing group's groupId", StringComparison.Ordinal));
        Assert.EndsWith(prompt[prompt.LastIndexOf('\n')..], request.Prompt, StringComparison.Ordinal);
        Assert.Equal(original, schema.ToJsonString());
        Assert.Null(state.Construction.Compact!.CurrentStep?.GroupId);
        Assert.Null(state.PendingCall);
    }

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
