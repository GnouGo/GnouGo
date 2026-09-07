using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class RecursiveSchemaValidationTests
{
    [Theory]
    [InlineData("anyOf")]
    [InlineData("oneOf")]
    public void RecursiveVariantsValidateEachNestedInstanceInsteadOfSkippingRepeatedSchemaPaths(string composition)
    {
        var schema = JsonNode.Parse("""
            {"$ref":"#/$defs/value","$defs":{"value":{}}}
            """)!.AsObject();
        schema["$defs"]!["value"]![composition] = JsonNode.Parse("""
            [
              {"type":"object","properties":{"kind":{"enum":["leaf"]},"value":{"enum":["allowed"]}},"required":["kind","value"],"additionalProperties":false},
              {"type":"object","properties":{"kind":{"enum":["branch"]},"children":{"type":"array","items":{"$ref":"#/$defs/value"}}},"required":["kind","children"],"additionalProperties":false}
            ]
            """);
        JsonNode Tree(string value)
        {
            JsonNode node = new JsonObject { ["kind"] = "leaf", ["value"] = value };
            for (var i = 0; i < 6; i++) node = new JsonObject { ["kind"] = "branch", ["children"] = new JsonArray(node) };
            return node;
        }
        Assert.Empty(PlanningContractValidation.ValidateInstance(Tree("allowed"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Tree("invented"), schema));
    }
}
