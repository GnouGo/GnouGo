using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class JsonSchemaTupleTests
{
    [Theory]
    [InlineData("[\"a\",1]", true)]
    [InlineData("[\"a\"]", true)]
    [InlineData("[\"a\",1,2]", false)]
    [InlineData("[1,\"a\"]", false)]
    public void TupleItemsAndClosedTailAreEnforced(string input, bool valid)
    {
        var schema = JsonNode.Parse("""{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false}""")!;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema));
        Assert.Equal(valid, PlanningContractValidation.ValidateInstance(JsonNode.Parse(input), schema).Count == 0);
    }

    [Fact]
    public void BooleanAlternativesAndDeclaredPropertiesAreNotIgnored()
    {
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create("a"), JsonNode.Parse("""{"oneOf":[true,{"type":"string"}]}""")!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create("a"), JsonNode.Parse("""{"allOf":[false,{"type":"string"}]}""")!));
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"allowed":1}"""), JsonNode.Parse("""{"type":"object","properties":{"allowed":true},"additionalProperties":false}""")!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"forbidden":1}"""), JsonNode.Parse("""{"type":"object","properties":{"forbidden":false}}""")!));
    }
}
