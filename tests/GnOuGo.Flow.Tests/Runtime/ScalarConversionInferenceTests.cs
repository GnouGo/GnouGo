using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class ScalarConversionInferenceTests
{
    [Theory]
    [InlineData("String")]
    [InlineData("encodeURI")]
    [InlineData("decodeURI")]
    [InlineData("encodeURIComponent")]
    [InlineData("decodeURIComponent")]
    public void ScalarUnionsEstablishOnlySuccessfulStringResults(string function)
    {
        foreach (var schema in new[] { "{\"type\":\"string\"}", "{\"type\":\"null\"}", "{\"type\":[\"string\",\"number\",\"boolean\",\"null\"]}", "{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"null\"}]}" })
        {
            var args = new Dictionary<string, JsonObject> { ["value"] = JsonNode.Parse(schema)!.AsObject() };
            Assert.Equal("string", ExpressionContractInference.Infer(function + "(value).replace(/x/g, 'y')", args)!["type"]!.ToString());
            Assert.Equal(schema, args["value"].ToJsonString());
        }
        Assert.Contains(function, ComputationInferenceProfile.Guidance);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"x-gnougo-opaque\":true}")]
    [InlineData("{\"type\":\"object\"}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    [InlineData("{\"anyOf\":[{\"type\":\"string\"},{}]}")]
    public void ContainersAndOpaqueAlternativesCannotBeCoercedIntoProof(string schema)
    {
        var args = new Dictionary<string, JsonObject> { ["value"] = JsonNode.Parse(schema)!.AsObject() };
        Assert.Null(ExpressionContractInference.Infer("String(value)", args));
        Assert.Null(ExpressionContractInference.Infer("decodeURIComponent(value)", args));
    }

    [Theory]
    [InlineData("String()")]
    [InlineData("String(/x/)")]
    [InlineData("String(1n)")]
    [InlineData("String(value, value)")]
    [InlineData("String(...[value])")]
    [InlineData("String?.(value)")]
    [InlineData("new String(value)")]
    [InlineData("globalThis.String(value)")]
    [InlineData("(() => { const convert = String; return convert(value); })()")]
    [InlineData("(() => { const result = String(value); const String = value; return result; })()")]
    [InlineData("(() => { const String = value; return String(value); })()")]
    [InlineData("(() => { const {String} = value; return String(value); })()")]
    [InlineData("[value].map(String => String(value))[0]")]
    [InlineData("(() => { String = value; return String(value); })()")]
    [InlineData("flag ? (String = value) : String(value)")]
    [InlineData("flag ? (globalThis.String = value) : String(value)")]
    [InlineData("Number(value)")]
    [InlineData("parseInt(value)")]
    public void UnsupportedCallsOrUnprovenIntrinsicIdentityRemainOpaque(string expression)
    {
        var result = ExpressionContractInference.Infer(expression, new Dictionary<string, JsonObject> { ["value"] = new() { ["type"] = "string" }, ["flag"] = new() { ["type"] = "boolean" } });
        // Composite unions may retain a known alternative, but must retain their opaque alternative.
        Assert.True(result is null || result.ToJsonString().Contains("x-gnougo-opaque", StringComparison.Ordinal), result?.ToJsonString());
    }

    [Fact]
    public void CaptureAliasesAndCoercionKeepKnownStringsWithoutInferringGuardedResultObjects()
    {
        var args = new Dictionary<string, JsonObject> { ["text"] = new() { ["type"] = "string" } };
        Assert.Equal("string", ExpressionContractInference.Infer("(() => { const m = String(text).match(/(a)/); const capture = decodeURIComponent(m[1]); return capture.replace(/a/g, 'x'); })()", args)!["type"]!.ToString());
        var guarded = ExpressionContractInference.Infer("(() => { const m = text.match(/(a)/); if (!m) throw new Error('missing'); return { value: decodeURIComponent(m[1]) }; })()", args);
        Assert.True(guarded is null || guarded.ToJsonString().Contains("x-gnougo-opaque", StringComparison.Ordinal));
        args["String"] = new() { ["type"] = "string" };
        Assert.Null(ExpressionContractInference.Infer("String(text)", args));
    }
}
