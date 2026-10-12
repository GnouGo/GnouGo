using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
    private static JsonObject ValueContracts() => JsonNode.Parse("""
        {"label":{"origin":"first","schema":{"type":"string","minLength":1},"source":"label","order":0,"instance_path":"/value"},
         "authorized":{"origin":"guard","schema":{"const":true},"source":"authorized","order":1,"instance_path":"/value"},
         "chosen":{"origin":"assembly","schema":{"type":"string"},"source":"label","order":2,"instance_path":"/chosen"},
         "chosen_2":{"origin":"export","schema":{"type":"string"},"source":"label","order":3,"instance_path":"/chosen"}}
        """)!.AsObject();

    [Theory]
    [InlineData("observed", true, null, 4)]
    [InlineData("", false, "first", 1)]
    [InlineData("observed", false, "guard", 2)]
    public void DirectValuesKeepEveryAliasCheckInOriginalOrder(string label, bool authorized, string? failure, int count)
    {
        const string program = "(()=>{const label=data.inputs.label;const authorized=data.inputs.authorized;return {chosen:label};})()";
        var contracts = ValueContracts(); var seen = new List<string>();
        ExpressionEvaluator.ValidateExpressionContracts(JsonValue.Create("${" + program + "}"), new() { [""] = contracts.DeepClone() });
        var context = new JsonObject { ["inputs"] = new JsonObject { ["label"] = label, ["authorized"] = authorized } };
        JsonNode? Run() => new ExpressionEvaluator().EvaluateCheckedExpression(program, context, contracts, observeCheck: (name, origin, error) => seen.Add(origin));
        if (failure is null) Assert.Equal(label, Run()!["chosen"]!.GetValue<string>());
        else
        {
            var error = Assert.Throws<WorkflowRuntimeException>(Run);
            Assert.Equal(failure, error.Details!["location"]!.GetValue<string>());
            Assert.Contains("$.value:", error.Message);
        }
        Assert.Equal(new[] { "first", "guard", "assembly", "export" }.Take(count), seen);
    }

    [Theory]
    [InlineData("9007199254740993", "{\"type\":\"integer\"}")]
    [InlineData("1.23000000000000000000001", "{\"type\":\"number\"}")]
    [InlineData("null", "{\"type\":[\"string\",\"null\"]}")]
    [InlineData("[null,1,1,[2]]", "{\"type\":\"array\"}")]
    [InlineData("{\"a\":null}", "{\"type\":\"object\"}")]
    public void DirectChecksPreserveExactValuesWithoutWholeContextImports(string json, string schema)
    {
        const string program = "(()=>{const observed=data.inputs.observed;return observed;})()";
        var contracts = new JsonObject { ["observed"] = new JsonObject { ["source"] = "observed", ["order"] = 0,
            ["origin"] = "original", ["schema"] = JsonNode.Parse(schema) } };
        var context = new JsonObject { ["inputs"] = new JsonObject { ["observed"] = JsonNode.Parse(json) }, ["unrelated"] = new string('x', 1_200_000) };
        var allowance = new JintSandbox.MappingAllowance(100000, TimeSpan.FromSeconds(15), 50000000);
        var result = new ExpressionEvaluator().EvaluateCheckedExpression(program, context, contracts, allowance);
        Assert.Equal(json, result?.ToJsonString() ?? "null");
        Assert.InRange(allowance.Snapshot()["materialized_bytes"]!.GetValue<long>(), 0, 10000);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate_order")]
    [InlineData("forward")]
    [InlineData("missing_declaration")]
    [InlineData("mixed")]
    [InlineData("path")]
    public void OrderedMetadataRejectsAmbiguousOrExecutableSources(string kind)
    {
        var contracts = ValueContracts();
        switch (kind)
        {
            case "unknown": contracts["chosen"]!["source"] = "data.inputs.label"; break;
            case "duplicate_order": contracts["chosen"]!["order"] = 1; break;
            case "forward": contracts["label"]!["source"] = "authorized"; break;
            case "missing_declaration": contracts.Remove("authorized"); break;
            case "mixed": contracts["label"]!.AsObject().Remove("source"); break;
            case "path": contracts["chosen"]!["instance_path"] = "/invalid~2"; break;
        }
        Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.ValidateExpressionContracts(
            JsonValue.Create("${(()=>{const label=data.inputs.label;const authorized=data.inputs.authorized;return label;})()}"), new() { [""] = contracts }));
    }

    [Fact]
    public void UnusedAliasStillFailsAndHistoricalScalarChecksRemainRejected()
    {
        var contracts = ValueContracts(); contracts["chosen"]!["schema"] = JsonNode.Parse("""{"const":"different"}""");
        var error = Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator().EvaluateCheckedExpression(
            "(()=>{const label=data.inputs.label;const authorized=data.inputs.authorized;return data.inputs.absent;})()",
            JsonNode.Parse("""{"inputs":{"label":"observed","authorized":true}}"""), contracts));
        Assert.Equal("assembly", error.Details!["location"]!.GetValue<string>());
        Assert.DoesNotContain("absent", error.Message);
        var historical = new JsonObject { ["value"] = new JsonObject { ["origin"] = "old", ["schema"] = new JsonObject { ["type"] = "string" } } };
        Assert.Contains("set input must be object", Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator().EvaluateCheckedExpression(
            "(()=>{const value=data.inputs.value;return value;})()", JsonNode.Parse("""{"inputs":{"value":"observed"}}"""), historical)).Message);
    }
}
