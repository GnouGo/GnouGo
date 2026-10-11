using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
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
