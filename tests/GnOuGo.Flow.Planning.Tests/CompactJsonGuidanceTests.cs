using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactJsonGuidanceTests
{
    [Theory]
    [InlineData("Choose a permitted action.")]
    [InlineData("Preserve this original:\n{\"businessText\":\"Keep the requested text exactly.\"}\nThen select a permitted action.")]
    public void SyntaxGuidancePreservesTheEntireOriginalPromptAndContext(string instruction)
    {
        var context = new JsonObject { ["original"] = "Exact user text including spaces", ["allowed"] = new JsonArray("first", "second") }.ToJsonString();
        var prompt = instruction + "\n" + context;
        var presented = PlanningPromptPresentation.PresentCompactJsonGuidance(prompt);
        Assert.StartsWith(instruction, presented, StringComparison.Ordinal);
        Assert.EndsWith("\n" + context, presented, StringComparison.Ordinal);
        Assert.Contains("without formatting whitespace outside strings", presented, StringComparison.Ordinal);
        Assert.Equal(JsonNode.Parse(context)!.ToJsonString(), JsonNode.Parse(presented[(presented.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!.ToJsonString());
    }
}
