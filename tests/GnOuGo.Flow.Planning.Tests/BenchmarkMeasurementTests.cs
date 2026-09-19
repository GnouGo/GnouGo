using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;
public sealed class BenchmarkMeasurementTests
{
    [Fact]
    public void SelectionRejectsUnknownAndDuplicateCases()
    {
        Assert.Equal(7, PlanningBenchmarkMeasurements.Select(null).Length);
        Assert.DoesNotContain("nullable_defaults", PlanningBenchmarkMeasurements.Select(null));
        Assert.Equal(["local", "conditional"], PlanningBenchmarkMeasurements.Select("local,conditional"));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.Select("local,local"));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.Select("invented"));
    }
    [Fact]
    public void ExactGatesIncludeFailuresAndRequireOneCompleteRevision()
    {
        var rows = PlanningBenchmarkMeasurements.CandidateCases.SelectMany(name => Enumerable.Range(1, 3).Select(i => Row(name, i))).ToArray();
        rows[0]["final_review"] = false; rows[1]["final_review"] = false;
        foreach (var row in rows.Skip(2).Take(3)) row["calls"] = 3;
        Assert.True(PlanningBenchmarkMeasurements.Summary(rows, "measured")["gates_passed"]!.GetValue<bool>());
        rows[5]["calls"] = 3;
        Assert.False(PlanningBenchmarkMeasurements.Summary(rows, "measured")["gates_passed"]!.GetValue<bool>());
        rows[5]["calls"] = 1; rows[5]["execution_correct"] = false;
        Assert.False(PlanningBenchmarkMeasurements.Summary(rows, "measured")["gates_passed"]!.GetValue<bool>());
        rows[5]["execution_correct"] = true; rows[5]["source_commit"] = "different";
        Assert.False(PlanningBenchmarkMeasurements.Summary(rows, "measured")["coverage_complete"]!.GetValue<bool>());
        Assert.False(PlanningBenchmarkMeasurements.Summary(rows.Take(7).ToArray(), "measured")["gates_passed"]!.GetValue<bool>());
    }
    [Fact]
    public void ReceiptReplayIsIdempotentAndUnknownUsageIsNotZero()
    {
        var run = new JsonObject { ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true };
        var usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 20, ["benchmark_cost_eur"] = .1m };
        PlanningBenchmarkMeasurements.RecordUsage(run, "one", usage); PlanningBenchmarkMeasurements.RecordUsage(run, "one", usage);
        Assert.Equal(10L, PlanningBenchmarkMeasurements.Usage(run, true)["input_tokens"]!.GetValue<long>());
        PlanningBenchmarkMeasurements.RecordUsage(run, "uncertain", null);
        var summary = PlanningBenchmarkMeasurements.Usage(run, true);
        Assert.Null(summary["input_tokens"]); Assert.Null(summary["estimated_cost_eur"]); Assert.False(summary["usage_complete"]!.GetValue<bool>());
        Assert.Equal(.1m, summary["known_cost_eur"]!.GetValue<decimal>());
    }
    [Fact]
    public void CorrectedFailuresRemainInDiagnosticHistory()
    {
        var run = new JsonObject { ["diagnostic_history"] = new JsonArray() };
        var state = new PlanningSession { ModelCalls = 1, Diagnostics = [new("INTENT_SCHEMA_INVALID", "/operations", "Missing business field")] };
        PlanningBenchmarkMeasurements.Capture(run, state); PlanningBenchmarkMeasurements.Capture(run, state);
        state.ModelCalls = 2; state.Status = PlanningStatus.Generating; state.PendingCall = new();
        PlanningBenchmarkMeasurements.Capture(run, state);
        state.PendingCall = null; state.Diagnostics.Clear(); PlanningBenchmarkMeasurements.Capture(run, state);
        var entry = Assert.Single(run["diagnostic_history"]!.AsArray()); Assert.Equal("invalid_intent", entry!["category"]!.ToString());
        Assert.Equal("provider_transport_failure", PlanningBenchmarkMeasurements.Category("MODEL_OUTPUT_LIMIT"));
        Assert.Equal("deterministic_builder_defect", PlanningBenchmarkMeasurements.Category("PLANNING_HOST_CONTRACT"));
    }
    private static JsonObject Row(string name, int repetition) => new() { ["source_commit"] = "frozen", ["case"] = name, ["repetition"] = repetition,
        ["calls"] = 1, ["final_review"] = true, ["first_pass_valid"] = true, ["execution_correct"] = true, ["safety_violations"] = new JsonArray() };
}
