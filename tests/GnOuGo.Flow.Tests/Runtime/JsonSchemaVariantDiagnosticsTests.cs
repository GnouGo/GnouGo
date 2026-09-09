using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class JsonSchemaVariantDiagnosticsTests
{
    [Theory]
    [InlineData("field/with~escapes.and[0]:suffix")]
    [InlineData("champ/avec~echappements.et[0]:suite")]
    public void InstanceFindingsCarryExactPointersWithoutParsingDisplayMessages(string field)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object",
            ["properties"] = new JsonObject { [field] = new JsonObject { ["type"] = "boolean" } }, ["required"] = new JsonArray(field), ["additionalProperties"] = false } };
        var pointer = "/0/" + field.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        foreach (var value in new[] { new JsonArray(new JsonObject { [field] = "invalid" }), new JsonArray(new JsonObject()) })
        {
            var finding = Assert.Single(PlanningContractValidation.ValidateInstanceFindings(value, schema));
            Assert.Equal(pointer, finding.InstancePointer);
            Assert.Equal(Assert.Single(PlanningContractValidation.ValidateInstance(value, schema)), finding.Message);
        }
        Assert.Equal("", Assert.Single(PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(false), schema)).InstancePointer);
    }

    [Theory]
    [InlineData("kind", "array", "scalar")]
    [InlineData("category", "collection", "value")]
    public void LiteralTagsExposeNestedFindingsWithoutChangingValidation(string tag, string array, string scalar)
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"payload":{"anyOf":[{"$ref":"#/$defs/array"},{"$ref":"#/$defs/scalar"}]}},
             "$defs":{"array":{"type":"object","properties":{"TAG":{"enum":["ARRAY"]},"items":{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"]}},"required":["TAG","items"],"additionalProperties":false},
                       "scalar":{"type":"object","properties":{"TAG":{"enum":["SCALAR"]},"value":{"type":"string"}},"required":["TAG","value"],"additionalProperties":false}}}
            """.Replace("TAG", tag, StringComparison.Ordinal).Replace("ARRAY", array, StringComparison.Ordinal).Replace("SCALAR", scalar, StringComparison.Ordinal))!;
        var value = new JsonObject { ["payload"] = new JsonObject { [tag] = array, ["items"] = new JsonObject { ["value"] = "invalid" }, ["properties"] = new JsonArray("misplaced") } };
        var findings = PlanningContractValidation.ValidateInstance(value, schema);
        Assert.Contains(findings, f => f.StartsWith("$.payload.items.value:", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.StartsWith("$.payload.properties:", StringComparison.Ordinal));
        value["payload"]!["items"]!["value"] = 1; value["payload"]!.AsObject().Remove("properties");
        Assert.Empty(PlanningContractValidation.ValidateInstance(value, schema));
    }

    [Fact]
    public void AmbiguousTagsDoNotSelectAnArbitraryVariantForRepair()
    {
        var schema = JsonNode.Parse("""
            {"anyOf":[
              {"type":"object","properties":{"kind":{"enum":["same"]},"value":{"type":"string"}}},
              {"type":"object","properties":{"kind":{"enum":["same"]},"value":{"type":"integer"}}}
            ]}
            """)!;
        var finding = Assert.Single(PlanningContractValidation.ValidateInstance(new JsonObject { ["kind"] = "same", ["value"] = true }, schema));
        Assert.Equal("$: value does not match any allowed schema variant", finding);
    }
}
