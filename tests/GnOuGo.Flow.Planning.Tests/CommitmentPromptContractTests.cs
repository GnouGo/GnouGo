using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CommitmentPromptContractTests
{
    [Fact]
    public void SharedPresentationPreservesEveryConstraintAndLiteralAndKeepsExistingReferenceBases()
    {
        var repeated = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"},"nullable":{"type":["number","null"]}},"required":["label","nullable"],"additionalProperties":false,"default":{"type":"string","required":["literal"]},"description":"Keep this explanatory contract annotation"}""")!;
        var existing = JsonNode.Parse("""{"$defs":{"s0":{"type":"string","enum":["opaque"]}},"$ref":"#/$defs/s0"}""")!;
        var context = new JsonObject { ["operations"] = new JsonArray(new JsonObject
        {
            ["id"] = "arbitrary_operation", ["inputs"] = new JsonArray(new JsonObject { ["name"] = "first", ["type"] = repeated.DeepClone() },
                new JsonObject { ["name"] = "second", ["type"] = repeated.DeepClone() }),
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "result", ["type"] = repeated.DeepClone() },
                new JsonObject { ["name"] = "scoped", ["type"] = existing.DeepClone() })
        }) };
        var before = context.DeepClone(); var bytes = context.ToJsonString().Length;
        PlanningPrompt.ShareOperationSchemas(context);
        Assert.True(context.ToJsonString().Length < bytes);
        Assert.NotEmpty(context["schemas"]!.AsObject());
        var operation = context["operations"]![0]!;
        Assert.True(JsonNode.DeepEquals(existing, operation["outputs"]![1]!["type"]));
        foreach (var key in new[] { "inputs", "outputs" })
            for (var i = 0; i < operation[key]!.AsArray().Count; i++)
                Assert.True(JsonNode.DeepEquals(before["operations"]![0]![key]![i]!["type"], Expand(operation[key]![i]!["type"]!, context)));
    }

    internal static JsonNode Expand(JsonNode schema, JsonNode context)
    {
        var result = schema.DeepClone();
        if (result is not JsonObject obj) return result;
        foreach (var (_, node) in PlanningSchemaReferences.Walk(obj, "", 0).ToArray())
            if (node["$ref"]?.ToString() is { } reference && reference.StartsWith("#/schemas/", StringComparison.Ordinal))
            {
                var replacement = context["schemas"]![reference[10..]]!.DeepClone();
                if (node.Parent is JsonObject parent) parent[parent.Single(p => ReferenceEquals(p.Value, node)).Key] = replacement;
                else if (node.Parent is JsonArray array) array[array.IndexOf(node)] = replacement;
                else result = replacement;
            }
        return result;
    }
}
