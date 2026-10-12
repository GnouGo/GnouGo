using System.Diagnostics;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
    // Frozen actual output of FusedBindingCompilationTests.LocalReuse.
    [Theory]
    [InlineData("array-reads", "present", true)]
    [InlineData("array-reads-stricter", "present", false)]
    [InlineData("array-reads", "invalid", false)]
    [InlineData("nullable-lookup", "id:one", true)]
    [InlineData("nullable-lookup", null, true)]
    [InlineData("nullable-lookup", "missing", false)]
    [InlineData("nullable-lookup", "unknown", false)]
    public void LocalReuseKeepsReachedChecksErrorsAndBoundedImports(string fixture, string? selected, bool success)
    {
        var measurements = new JsonArray();
        foreach (var bytes in new[] { 1, 1_200_000 })
        foreach (var warm in new[] { false, true })
        {
            string[]? previousEvents = null; string? previousOutcome = null;
            foreach (var profile in new[] { "v14", "v15" })
            {
                using var stream = typeof(ExpressionContractTests).Assembly.GetManifestResourceStream(
                    $"GnOuGo.Flow.Tests.Expressions.Fixtures.{fixture}-{profile}.yaml")!;
                using var reader = new StreamReader(stream);
                var definition = WorkflowParser.Parse(reader.ReadToEnd()).Workflows["main"].Outputs!.Values.Single();
                var program = ExpressionSegments.Read(definition.Expr!).Single().Expression;
                var inputs = fixture.StartsWith("array", StringComparison.Ordinal)
                    ? JsonNode.Parse("{\"searchPages\":[[9007199254740993.00,null,1.23000000000000000000001]]}")!.AsObject()
                    : JsonNode.Parse("""{"selected":null,"offeredBlocks":[{"reference":"id:one","text":"  Complete\ntext\t é  "}],"originalBlocks":[{"reference":"id:one","text":"  Complete\ntext\t é  "},{"reference":"other","text":"unrelated"}]}""")!.AsObject();
                if (selected == "invalid") inputs["searchPages"] = JsonNode.Parse("[[false]]");
                if (fixture == "nullable-lookup")
                {
                    inputs["selected"] = selected;
                    if (selected == "missing") inputs.Remove("selected");
                }
                var context = new JsonObject { ["inputs"] = inputs, ["unusedObservation"] = new string('x', bytes) };
                var original = context.ToJsonString();
                var allowance = new JintSandbox.MappingAllowance(10_000, TimeSpan.FromSeconds(5), 50_000_000);
                var events = new List<string>(); var evaluator = new ExpressionEvaluator();
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                JsonNode? result = null;
                var error = Record.Exception(() => result = evaluator.EvaluateCheckedExpression(program, context,
                    definition.ExpressionContracts![""]!.AsObject(), allowance,
                    (_, origin, code) => events.Add(origin + ":" + (code ?? "passed"))));
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                string outcome;
                if (success)
                {
                    Assert.Null(error); outcome = result!.ToJsonString();
                    if (fixture.StartsWith("array", StringComparison.Ordinal))
                        Assert.Equal("[9007199254740993.00,null,1.23000000000000000000001]", outcome);
                    else Assert.Equal(selected is null ? null : "  Complete\ntext\t é  ", result![0]?.GetValue<string>());
                }
                else
                {
                    var failure = Assert.IsType<WorkflowRuntimeException>(error);
                    outcome = failure.Code + ":" + failure.Details?["location"] + ":" + failure.Message;
                }
                if (previousOutcome is not null) Assert.Equal(previousOutcome, outcome);
                if (previousEvents is not null) Assert.Equal(previousEvents, events);
                previousOutcome = outcome; previousEvents = events.ToArray();
                Assert.Equal(original, context.ToJsonString());
                Assert.DoesNotContain(events, e => e.Contains("Complete", StringComparison.Ordinal));
                if (selected == "unknown") Assert.DoesNotContain(events, e => e.StartsWith("original:", StringComparison.Ordinal));
                var counters = allowance.Snapshot();
                Assert.InRange(counters["materialized_bytes"]!.GetValue<long>(), 0, 50_000);
                counters["profile"] = profile; counters["unused_observation_bytes"] = bytes; counters["warm"] = warm;
                counters["allocated_bytes"] = allocated; counters["elapsed_ms"] = elapsed;
                counters["checks"] = new JsonArray(events.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
                measurements.Add(counters);
            }
        }
        if (Environment.GetEnvironmentVariable("GNOU_GO_V15_RESOURCES") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"{fixture}-{selected ?? "null"}.json"), measurements.ToJsonString(new() { WriteIndented = true }));
        }
    }
}
