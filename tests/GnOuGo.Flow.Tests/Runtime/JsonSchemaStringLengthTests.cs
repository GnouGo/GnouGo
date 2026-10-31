using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class JsonSchemaStringLengthTests
{
    [Fact]
    public void CapturedUnicodeModeAcceptsExactly256CharactersWithoutChangingHistoricalDefault()
    {
        var schema = JsonNode.Parse("""{"type":"string","minLength":256,"maxLength":256}""")!;
        var text = new string('a', 255) + "🌍";
        Assert.Equal(257, text.Length);
        Assert.Empty(JsonSchemaInstanceValidator.ValidateInstanceFindings(JsonValue.Create(text), schema, unicodeStringLengths: true));
        Assert.Equal("maxLength", Assert.Single(JsonSchemaInstanceValidator.ValidateInstanceFindings(JsonValue.Create(text), schema)).Rule);
        Assert.Equal("maxLength", Assert.Single(PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(text), schema)).Rule);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create(text), schema));
        Assert.Equal("maxLength", Assert.Single(JsonSchemaInstanceValidator.ValidateInstanceFindings(JsonValue.Create(text + "a"), schema, true)).Rule);
        Assert.Equal("minLength", Assert.Single(JsonSchemaInstanceValidator.ValidateInstanceFindings(JsonValue.Create("🌍" + new string('a', 254)), schema, true)).Rule);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("ab", 2)]
    [InlineData("é", 1)]
    [InlineData("e\u0301", 2)]
    [InlineData("🌍", 1)]
    [InlineData("🌍🌍", 2)]
    public void UnicodeModeCountsCodePointsWithoutNormalizingText(string text, int characters)
    {
        var schema = new JsonObject { ["type"] = "string", ["minLength"] = characters, ["maxLength"] = characters };
        Assert.Empty(JsonSchemaInstanceValidator.ValidateInstanceFindings(JsonValue.Create(text), schema, true));
        schema["minLength"] = characters + 1;
        Assert.Equal("minLength", Assert.Single(JsonSchemaInstanceValidator.ValidateInstanceFindings(JsonValue.Create(text), schema, true)).Rule);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"value":{"$ref":"#/$defs/text"}}}""", """{"value":"🌍"}""", "/value")]
    [InlineData("""{"type":"object","additionalProperties":{"$ref":"#/$defs/text"}}""", """{"extra":"🌍"}""", "/extra")]
    [InlineData("""{"type":"array","items":{"$ref":"#/$defs/text"}}""", """["🌍"]""", "/0")]
    [InlineData("""{"type":"array","prefixItems":[{"$ref":"#/$defs/text"}],"items":false}""", """["🌍"]""", "/0")]
    [InlineData("""{"allOf":[{"$ref":"#/$defs/text"}]}""", "\"🌍\"", "")]
    [InlineData("""{"anyOf":[{"$ref":"#/$defs/text"},{"type":"integer"}]}""", "\"🌍\"", "")]
    [InlineData("""{"oneOf":[{"$ref":"#/$defs/text"},{"type":"integer"}]}""", "\"🌍\"", "")]
    [InlineData("""{"if":{"type":"string","maxLength":1},"then":{"$ref":"#/$defs/text"},"else":{"const":"unreachable"}}""", "\"🌍\"", "")]
    [InlineData("""{"if":{"type":"integer"},"then":{"const":0},"else":{"$ref":"#/$defs/text"}}""", "\"🌍\"", "")]
    public void UnicodeModePropagatesThroughExistingRecursiveValidation(string contract, string instance, string pointer)
    {
        var schema = JsonNode.Parse(contract)!.AsObject();
        schema["$defs"] = JsonNode.Parse("""{"text":{"$ref":"#/$defs/bounded"},"bounded":{"type":"string","minLength":1,"maxLength":1}}""");
        var value = JsonNode.Parse(instance);
        Assert.Empty(JsonSchemaInstanceValidator.ValidateInstanceFindings(value, schema, true));
        var historical = JsonSchemaInstanceValidator.ValidateInstanceFindings(value, schema);
        Assert.NotEmpty(historical);
        Assert.All(historical, finding => Assert.Equal(pointer, finding.InstancePointer));
        Assert.Equal(historical, JsonSchemaInstanceValidator.ValidateInstanceFindings(value, schema, false));
    }

    [Fact]
    public void SelectedAlternativeDiagnosticsKeepUnicodeModeAndExactEscapedPointer()
    {
        var schema = JsonNode.Parse("""
            {"anyOf":[
              {"type":"object","properties":{"kind":{"const":"text"},"a/b~c":{"type":"string","minLength":2}},"required":["kind","a/b~c"]},
              {"type":"object","properties":{"kind":{"const":"number"},"value":{"type":"number"}},"required":["kind","value"]}]}
            """)!;
        var instance = JsonNode.Parse("""{"kind":"text","a/b~c":"🌍"}""");
        Assert.Empty(JsonSchemaInstanceValidator.ValidateInstanceFindings(instance, schema));
        var finding = Assert.Single(JsonSchemaInstanceValidator.ValidateInstanceFindings(instance, schema, "/original", true));
        Assert.Equal("/original/a~1b~0c", finding.InstancePointer);
        Assert.Equal("minLength", finding.Rule);
    }

    [Fact]
    public void RuntimeAndPublicEntryPointsDoNotInferModeFromSchemaAnnotations()
    {
        var schema = JsonNode.Parse("""{"$comment":"Unicode code points","type":"string","maxLength":1}""")!.AsObject();
        var instance = JsonValue.Create("🌍");
        Assert.False(JsonSchemaInstanceValidator.MatchesSchema(instance, schema, schema));
        Assert.NotEmpty(JsonSchemaInstanceValidator.ValidateInstance(instance, schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(instance, schema));
        Assert.Empty(JsonSchemaInstanceValidator.ValidateInstanceFindings(instance, schema, true));
    }
}
