using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArrayAlternativePresentationTests
{
    [Fact]
    public void LiteralAndAnnotationObjectsAreNotReinterpretedAsSchemas()
    {
        var literal = JsonNode.Parse("""{"anyOf":[{"type":"array"},{"type":"array","maxItems":1},{"type":"null"}]}""")!;
        var source = new JsonObject { ["const"] = literal.DeepClone(), ["default"] = literal.DeepClone(),
            ["examples"] = new JsonArray(literal.DeepClone()), ["x-extension"] = literal.DeepClone(),
            ["$defs"] = new JsonObject { ["array"] = literal.DeepClone() } };
        var result = PlanningPromptPresentation.OrderBoundedArrayAlternativesFirst(source);
        foreach (var field in new[] {"const", "default", "examples", "x-extension"})
            Assert.True(JsonNode.DeepEquals(source[field], result[field]));
        Assert.Equal(1, result["$defs"]!["array"]!["anyOf"]![0]!["maxItems"]!.GetValue<int>());
        var mixed = JsonNode.Parse("""{"anyOf":[true,false,{"type":"array"},{"type":"array","maxItems":1}]}""")!;
        Assert.True(JsonNode.DeepEquals(mixed, PlanningPromptPresentation.OrderBoundedArrayAlternativesFirst(mixed)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"e_1\"]")]
    [InlineData("[\"e_10\"]")]
    [InlineData("[\"e_10\",\"e_10\"]")]
    [InlineData("[\"e_1\",\"e_1\"]")]
    [InlineData("[\"e_1\",\"e_10\"]")]
    [InlineData("[\"unknown\"]")]
    [InlineData("\"e_1\"")]
    public void OrderingRetainsTheExactInstanceLanguageAndDoesNotMutateTheIssuedSchema(string json)
    {
        var original = JsonNode.Parse("""
            {"anyOf":[
              {"type":"array","items":{"type":"string","enum":["e_10"]},"minItems":1},
              {"type":"array","items":{"type":"string","enum":["e_1"]},"minItems":1,"maxItems":1},
              {"type":"null"}]}
            """)!;
        var bytes = original.ToJsonString();
        var ordered = PlanningPromptPresentation.OrderBoundedArrayAlternativesFirst(original);
        Assert.Equal(bytes, original.ToJsonString());
        Assert.Equal(1, ordered["anyOf"]![0]!["maxItems"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(original["anyOf"]![1], ordered["anyOf"]![0]));
        Assert.True(JsonNode.DeepEquals(original["anyOf"]![0], ordered["anyOf"]![1]));
        Assert.True(JsonNode.DeepEquals(original["anyOf"]![2], ordered["anyOf"]![2]));
        var instance = JsonNode.Parse(json);
        Assert.Equal(PlanningContractValidation.ValidateInstance(instance, original).Count == 0,
            PlanningContractValidation.ValidateInstance(instance, ordered).Count == 0);
        Assert.True(JsonNode.DeepEquals(ordered, PlanningPromptPresentation.OrderBoundedArrayAlternativesFirst(ordered)));
    }
}
