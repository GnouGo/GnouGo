using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
    private const string LexicalProgram = """
        (() => {
          const key = data.inputs.key;
          const description = key === null ? (() => {
            const missing = [null];
            return missing;
          })() : (() => {
            const offered = m.lookup(data.inputs.offered, [key], "key");
            const original = m.lookup(data.inputs.original, [key], "key");
            const text = original.map(block => block.text);
            return text;
          })();
          return description;
        })()
        """;

    private static JsonObject LexicalContracts() => JsonNode.Parse("""
        {
          "key":{"scope":"block0","order":0,"source":"key","origin":"choose","schema":{"type":["string","null"]}},
          "description":{"scope":"block0","order":1,"source":"description","origin":"assemble","schema":{"type":"array","items":{"type":["string","null"]}}},
          "missing":{"scope":"block1","order":0,"source":"missing","origin":"absent","schema":{"type":"array","items":{"type":"null"}}},
          "offered":{"scope":"block2","order":0,"source":"offered","origin":"offered","schema":{"type":"array"}},
          "original":{"scope":"block2","order":1,"source":"original","origin":"original","schema":{"type":"array"}},
          "text":{"scope":"block2","order":2,"source":"text","origin":"text","schema":{"type":"array","items":{"type":"string"}}}
        }
        """)!.AsObject();

    [Theory]
    [InlineData(null)]
    [InlineData("opaque:key")]
    [InlineData("unknown")]
    public void NativeBranchChecksRunOnlyOnTheSelectedPathInOrder(string? key)
    {
        var checks = LexicalContracts(); var seen = new List<string>();
        ExpressionEvaluator.ValidateExpressionContracts(JsonValue.Create("${" + LexicalProgram + "}"), new() { [""] = checks.DeepClone() });
        var context = JsonNode.Parse("""{"inputs":{"key":null,"offered":[{"key":"opaque:key"}],"original":[{"key":"opaque:key","text":"  Exact\ntext\t é  "}]}}""")!;
        context["inputs"]!["key"] = key;
        JsonNode? Run() => new ExpressionEvaluator().EvaluateCheckedExpression(LexicalProgram, context, checks,
            observeCheck: (name, origin, error) => seen.Add(origin));
        if (key == "unknown")
        {
            var error = Assert.Throws<WorkflowRuntimeException>(Run);
            Assert.Equal("offered", error.Details!["location"]!.GetValue<string>());
            Assert.Equal(new[] { "choose", "offered" }, seen);
        }
        else
        {
            Assert.Equal(key is null ? "[null]" : "[\"  Exact\\ntext\\t é  \"]", Run()!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Assert.Equal(key is null ? ["choose", "absent", "assemble"] : new[] { "choose", "offered", "original", "text", "assemble" }, seen);
        }
    }

    [Theory]
    [InlineData("block9")]
    [InlineData("block01")]
    [InlineData("data.inputs.key")]
    [InlineData("")]
    public void LexicalMetadataCannotSelectUnknownBlocksOrData(string scope)
    {
        var checks = LexicalContracts(); checks["missing"]!["scope"] = scope;
        Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.ValidateExpressionContracts(JsonValue.Create("${" + LexicalProgram + "}"), new() { [""] = checks }));
    }

    [Fact]
    public void InvalidUnusedLexicalCheckPreventsLaterEvaluationAndRetainsItsOrigin()
    {
        var checks = LexicalContracts(); checks["offered"]!["schema"] = JsonNode.Parse("""{"type":"array","maxItems":0}""");
        var seen = new List<string>();
        var error = Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator().EvaluateCheckedExpression(LexicalProgram,
            JsonNode.Parse("""{"inputs":{"key":"x","offered":[{"key":"x"}]}}"""), checks,
            observeCheck: (name, origin, code) => seen.Add(origin)));
        Assert.Equal("offered", error.Details!["location"]!.GetValue<string>());
        Assert.Equal(new[] { "choose", "offered" }, seen);
    }
    [Fact]
    public void LexicalChecksDoNotImportUnrelatedBrowserPayloads()
    {
        JsonObject? baseline = null;
        foreach (var size in new[] { 1, 1_200_000 })
        {
            var context = JsonNode.Parse("""{"inputs":{"key":"opaque:key","offered":[{"key":"opaque:key"}],"original":[{"key":"opaque:key","text":"exact"}]}}""")!.AsObject();
            context["unrelatedObservation"] = new string('x', size);
            var allowance = new GnOuGo.Flow.Core.Scripting.JintSandbox.MappingAllowance(10000, TimeSpan.FromSeconds(5), 50000000);
            var result = new ExpressionEvaluator().EvaluateCheckedExpression(LexicalProgram, context, LexicalContracts(), allowance);
            Assert.Equal("exact", result![0]!.GetValue<string>());
            var counters = allowance.Snapshot();
            Assert.Equal(50000000, counters["memory_limit_bytes"]!.GetValue<long>());
            if (baseline is not null)
            {
                Assert.Equal(baseline["statements"]!.ToJsonString(), counters["statements"]!.ToJsonString());
                Assert.Equal(baseline["materialized_bytes"]!.ToJsonString(), counters["materialized_bytes"]!.ToJsonString());
            }
            baseline = counters;
        }
    }

    [Fact]
    public void LexicalLookupValidatesTheSelectedOriginalWithinLargeObservations()
    {
        var original = new JsonArray(Enumerable.Range(0, 610).Select(i => (JsonNode)new JsonObject
        {
            ["key"] = "snapshot:opaque:" + i,
            ["text"] = "  Complete observed block " + i + "\n" + new string('x', 2000) + " é  ",
            ["number"] = JsonNode.Parse("9007199254740993"),
            ["optional"] = null
        }).ToArray());
        var context = new JsonObject { ["inputs"] = new JsonObject
        {
            ["key"] = "snapshot:opaque:609", ["offered"] = new JsonArray(new JsonObject { ["key"] = "snapshot:opaque:609" }),
            ["original"] = original
        } };
        var before = context.ToJsonString();
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(before), 1200000, 1400000);
        var allowance = new GnOuGo.Flow.Core.Scripting.JintSandbox.MappingAllowance(100000, TimeSpan.FromSeconds(15), 50000000);
        var result = new ExpressionEvaluator().EvaluateCheckedExpression(LexicalProgram, context, LexicalContracts(), allowance);
        Assert.Equal(original[609]!["text"]!.GetValue<string>(), result![0]!.GetValue<string>());
        Assert.Equal(before, context.ToJsonString());
        Assert.InRange(allowance.Snapshot()["materialized_bytes"]!.GetValue<long>(), 1, 50000000);
    }

}
