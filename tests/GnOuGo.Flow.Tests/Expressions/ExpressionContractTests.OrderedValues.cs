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

    [Fact]
    public void LargeObservedArraysAvoidRepeatedCheckedWrapperMaterialization()
    {
        var records = new JsonArray(Enumerable.Range(0, 1200).Select(i => (JsonNode)new JsonObject {
            ["key"] = "snapshot:opaque:" + i, ["text"] = new string('x', 1000), ["optional"] = null }).ToArray());
        var input = new JsonObject { ["inputs"] = new JsonObject { ["records"] = records, ["authorized"] = true } };
        var arraySchema = JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"},"text":{"type":"string"},"optional":{"type":"null"}},"required":["key","text","optional"],"additionalProperties":false}}""")!;
        var old = new JsonObject(); var direct = new JsonObject();
        var names = new[] { "records", "authorized", "selected", "exported" };
        for (var i = 0; i < names.Length; i++)
        {
            var field = i < 2 ? "value" : "rows";
            var schema = i == 1 ? JsonNode.Parse("""{"const":true}""")! : arraySchema;
            old[names[i]] = new JsonObject { ["origin"] = names[i], ["schema"] = new JsonObject {
                ["type"] = "object", ["properties"] = new JsonObject { [field] = schema.DeepClone() }, ["required"] = new JsonArray(field), ["additionalProperties"] = false } };
            direct[names[i]] = new JsonObject { ["origin"] = names[i], ["schema"] = schema.DeepClone(),
                ["order"] = i, ["source"] = i == 1 ? "authorized" : "records", ["instance_path"] = "/" + field };
        }
        const string before = "(()=>{const records={value:data.inputs.records};const authorized={value:data.inputs.authorized};const selected={rows:records.value};const exported=selected;return exported;})()";
        const string after = "(()=>{const records=data.inputs.records;const authorized=data.inputs.authorized;return {rows:records};})()";
        var metrics = new JsonArray(); long priorAllocation = 0;
        foreach (var (profile, program, contracts) in new[] { ("v11", before, old), ("v12", after, direct) })
        {
            var evaluator = new ExpressionEvaluator();
            _ = evaluator.EvaluateCheckedExpression(program, input, contracts);
            var allowance = new JintSandbox.MappingAllowance(100000, TimeSpan.FromSeconds(15), 50000000);
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = evaluator.EvaluateCheckedExpression(program, input, contracts, allowance);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; timer.Stop();
            Assert.Equal(records.ToJsonString(), result!["rows"]!.ToJsonString());
            var counters = allowance.Snapshot();
            metrics.Add(new JsonObject { ["profile"] = profile, ["allocated_bytes"] = allocated, ["elapsed_ms"] = timer.Elapsed.TotalMilliseconds, ["sandbox"] = counters });
            if (priorAllocation != 0) Assert.True(allocated < priorAllocation, $"allocation {priorAllocation}→{allocated}");
            priorAllocation = allocated;
        }
        if (Environment.GetEnvironmentVariable("GNOU_GO_V12_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "large-array-resources.json"), metrics.ToJsonString());
        }
    }
}
