using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SchemaIntegerRepresentationTests
{
    [Theory]
    [InlineData("minLength", "string")]
    [InlineData("maxLength", "string")]
    [InlineData("minItems", "array")]
    [InlineData("maxItems", "array")]
    [InlineData("minProperties", "object")]
    [InlineData("maxProperties", "object")]
    public void IntegralKeywordsHaveTheSameMeaningAcrossNumericRepresentations(string keyword, string type)
    {
        foreach (var value in new JsonNode[] { JsonValue.Create(1)!, JsonValue.Create(1L)!, JsonValue.Create(1m)!, JsonValue.Create(1d)!, JsonNode.Parse("1.0")! })
        {
            var schema = new JsonObject { ["type"] = type, [keyword] = value };
            Assert.Empty(PlanningContractValidation.ValidateSchema(schema));
        }
        foreach (var value in new[] { -1m, 0.5m, decimal.MaxValue })
            Assert.NotEmpty(PlanningContractValidation.ValidateSchema(new JsonObject { ["type"] = type, [keyword] = value }));
    }
}
