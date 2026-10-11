using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CheckObservationPreservesResultsCountersAndFirstFailure(bool allowed)
    {
        const string program = "(()=>{const permission={value:data.inputs.allowed};const selected={value:data.inputs.exact};return selected;})()";
        var contracts = JsonNode.Parse("""{"permission":{"origin":"/guard","schema":{"type":"object","properties":{"value":{"const":true}},"required":["value"]}},"selected":{"origin":"/copy","schema":{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}}}""")!.AsObject();
        var context = new JsonObject { ["inputs"] = new JsonObject { ["allowed"] = allowed, ["exact"] = JsonNode.Parse("9007199254740993.00") } };
        var evaluator = new ExpressionEvaluator();
        string? previous = null;
        foreach (var traced in new[] { false, true })
        {
            var events = new List<(string Name, string Origin, string? Error)>();
            Action<string, string, string?>? observe = traced ? (name, origin, error) => events.Add((name, origin, error)) : null;
            var allowance = new JintSandbox.MappingAllowance(100000, TimeSpan.FromSeconds(15), 50000000);
            var before = GC.GetAllocatedBytesForCurrentThread(); var time = System.Diagnostics.Stopwatch.StartNew();
            if (allowed)
            {
                var result = evaluator.EvaluateCheckedExpression(program, context, contracts, allowance, observe);
                Assert.Equal("9007199254740993.00", result!["value"]!.ToJsonString());
            }
            else
            {
                var error = Assert.Throws<WorkflowRuntimeException>(() => evaluator.EvaluateCheckedExpression(program, context, contracts, allowance, observe));
                Assert.Equal("/guard", error.Details!["location"]!.GetValue<string>());
            }
            time.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(traced ? allowed ? 2 : 1 : 0, events.Count);
            if (traced) Assert.Equal(allowed ? null : "INPUT_VALIDATION", events[0].Error);
            var snapshot = allowance.Snapshot();
            var work = string.Join('|', new[] { "statements", "materialized_bytes", "output_bytes" }.Select(k => snapshot[k]!.ToJsonString()));
            if (previous is not null) Assert.Equal(previous, work); previous = work;
            TestContext.Current.TestOutputHelper?.WriteLine($"allowed={allowed}; trace={traced}; allocated={allocated}; elapsedMs={time.Elapsed.TotalMilliseconds}; work={work}; outcomes={events.Count}");
        }
    }

    [Fact]
    public void NativeProducerAliasesDoNotImportUnrelatedObservationsAndKeepExactCounters()
    {
        const string program = "(()=>{const searchPages={value:((searchResults)=>searchResults.pages)(data.steps.physical)};const products={products:searchPages.value.flatMap(page=>page)};return products;})()";
        var contracts = JsonNode.Parse("""{"searchPages":{"origin":"/tasks/pages","schema":{"type":"object","properties":{"value":{"type":"array","items":{"type":"array","items":{"type":["number","null"]}}}},"required":["value"]}},"products":{"origin":"/tasks/products","schema":{"type":"object","properties":{"products":{"type":"array","items":{"type":["number","null"]}}},"required":["products"]}}}""")!.AsObject();
        long allocation = 0; string? counters = null;
        foreach (var bytes in new[] { 0, 1_200_000 })
        {
            var data = new JsonObject { ["steps"] = new JsonObject { ["physical"] = new JsonObject {
                ["pages"] = JsonNode.Parse("[[9007199254740993,null],[],[1.23000000000000000000001,9007199254740993]]"),
                ["observations"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode?)JsonValue.Create(new string('x', bytes))).ToArray()) } } };
            var evaluator = new ExpressionEvaluator();
            evaluator.EvaluateCheckedExpression(program, data, contracts); // Warm parsing/JIT, not a mapping cache.
            var allowance = new JintSandbox.MappingAllowance(100000, TimeSpan.FromSeconds(15), 50000000);
            var start = GC.GetAllocatedBytesForCurrentThread();
            var result = evaluator.EvaluateCheckedExpression(program, data, contracts, allowance);
            var measured = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal("[9007199254740993,null,1.23000000000000000000001,9007199254740993]", result!["products"]!.ToJsonString());
            if (bytes == 0) allocation = measured; else Assert.InRange(measured, 0, allocation + 16384);
            var snapshot = allowance.Snapshot();
            // Timing is measured separately; deterministic work counters must
            // not grow with data that the expression never reads.
            var work = string.Join("|", new[] { "statements", "materialized_bytes", "output_bytes" }.Select(k => snapshot[k]?.ToJsonString()));
            if (counters is not null) Assert.Equal(counters, work); counters = work;
            Assert.InRange(snapshot["materialized_bytes"]!.GetValue<long>(), 1, 50000000);
            TestContext.Current.TestOutputHelper?.WriteLine($"unreadBytes={bytes * 8}; allocated={measured}; counters={snapshot.ToJsonString()}");
        }
    }
}
