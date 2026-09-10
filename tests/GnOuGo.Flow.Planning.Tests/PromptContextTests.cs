using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PromptContextTests
{

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
