using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class JsonSchemaVariantDiagnosticsTests
{
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
