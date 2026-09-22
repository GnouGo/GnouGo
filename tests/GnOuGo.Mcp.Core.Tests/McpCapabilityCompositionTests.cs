using System.Text.Json.Nodes;
using GnOuGo.Mcp.Core;

namespace GnOuGo.Mcp.Core.Tests;

public sealed class McpCapabilityCompositionTests
{
    [Fact]
    public void ParseAndValidate_AcceptsCompleteOperation()
    {
        var result = McpCapabilityCompositionParser.ParseAndValidate(JsonNode.Parse("""
            {
              "gnougo": {
                "composition": {
                  "version": 1,
                  "kind": "complete_operation",
                  "encapsulates": [
                    { "kind": "tool", "method": "review_start" },
                    { "kind": "tool", "method": "review_finish" }
                  ]
                }
              }
            }
            """));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(McpCapabilityCompositionMetadata.CompleteOperationKind, result.Contract!.Kind);
        Assert.Equal(2, result.Contract.Encapsulates.Count);
    }

    [Theory]
    [InlineData("{\"gnougo\":{\"composition\":[]}}")]
    [InlineData("{\"gnougo\":{\"composition\":{\"version\":2,\"kind\":\"complete_operation\",\"encapsulates\":[]}}}")]
    [InlineData("{\"gnougo\":{\"composition\":{\"version\":1,\"kind\":\"complete_operation\",\"encapsulates\":[{\"kind\":\"tool\",\"method\":\"phase\"},{\"kind\":\"tool\",\"method\":\"phase\"}]}}}")]
    public void ParseAndValidate_RejectsMalformedComposition(string json)
    {
        var result = McpCapabilityCompositionParser.ParseAndValidate(JsonNode.Parse(json));

        Assert.True(result.IsDeclared);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ParseAndValidate_TreatsMissingCompositionAsUndeclared()
    {
        var result = McpCapabilityCompositionParser.ParseAndValidate(new JsonObject());

        Assert.False(result.IsDeclared);
        Assert.Empty(result.Errors);
    }
}
