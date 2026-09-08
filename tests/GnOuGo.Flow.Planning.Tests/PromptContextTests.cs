using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PromptContextTests
{
    [Theory]
    [InlineData("field", "Declared observation")]
    [InlineData("champ", "Observation déclarée")]
    public void RepeatedContractsFitTheUnitBudgetWithoutLosingAnyConstraint(string prefix, string description)
    {
        var properties = new JsonObject();
        for (var i = 0; i < 60; i++) properties[prefix + i] = new JsonObject { ["type"] = new JsonArray("string", "null"),
            ["enum"] = new JsonArray("first", "second", null), ["description"] = description + " " + i + ": " + new string('x', 250) };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false,
            ["$defs"] = new JsonObject { ["local"] = new JsonObject { ["type"] = "string", ["minLength"] = 4 } },
            ["allOf"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { [prefix + "0"] = new JsonObject { ["$ref"] = "#/$defs/local" } } }) };
        var original = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject { ["source"] = prefix + i, ["resultContract"] = schema.DeepClone() }).ToArray());
        var before = original.ToJsonString(); var compact = PlanningPromptContext.Share(original);
        Assert.True(PlanningConstruction.EstimateInputTokens(before, new()) > 12000);
        Assert.InRange(PlanningConstruction.EstimateInputTokens(PlanningPromptContext.Instructions + compact.ToJsonString(), new()), 1, 12000);
        Assert.Equal(before, original.ToJsonString());
        Assert.True(JsonNode.DeepEquals(original, Expand(compact)));
        Assert.True(JsonNode.DeepEquals(compact, PlanningPromptContext.Share(original)));
    }

    [Fact]
    public void SmallContextsAndReservedLiteralKeysRemainUnchanged()
    {
        var simple = new JsonArray(new JsonObject { ["source"] = "one", ["resultContract"] = new JsonObject { ["type"] = "string" } });
        Assert.True(JsonNode.DeepEquals(simple, PlanningPromptContext.Share(simple)));
        var literal = new JsonObject { ["$contextRef"] = "literal value", ["description"] = new string('x', 2000) };
        var context = new JsonArray(literal, literal.DeepClone());
        Assert.True(JsonNode.DeepEquals(context, PlanningPromptContext.Share(context)));
    }

    private static JsonNode? Expand(JsonNode pooled)
    {
        if (pooled is not JsonObject envelope || envelope["shared"] is not JsonObject shared) return pooled.DeepClone();
        JsonNode? Read(JsonNode? node)
        {
            if (node is JsonObject obj && obj["$contextRef"] is JsonValue reference) return Read(shared[reference.GetValue<string>()]);
            if (node is JsonObject value) return new JsonObject(value.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Read(p.Value))));
            return node is JsonArray array ? new JsonArray(array.Select(Read).ToArray()) : node?.DeepClone();
        }
        return Read(envelope["context"]);
    }
}
