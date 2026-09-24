using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;
public sealed class BenchmarkMeasurementTests
{
    [Fact]
    public void RecoveredUsageIsBoundedButUnknownAndReplayDoesNotDoubleCount()
    {
        var run = new JsonObject { ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true };
        var receipt = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 2, ["benchmark_cost_eur"] = .1m,
            ["transport_attempts"] = 2, ["uncertain_attempts"] = 1, ["reserved_input_tokens"] = 96000,
            ["reserved_output_tokens"] = 32768, ["reserved_cost_eur"] = 4m, ["benchmark_usage_bounded"] = true };
        PlanningBenchmarkMeasurements.RecordUsage(run, "same", receipt); PlanningBenchmarkMeasurements.RecordUsage(run, "same", receipt);
        var usage = PlanningBenchmarkMeasurements.Usage(run, true);
        Assert.False(usage["usage_complete"]!.GetValue<bool>()); Assert.True(usage["usage_bounded"]!.GetValue<bool>());
        Assert.Null(usage["input_tokens"]); Assert.Null(usage["estimated_cost_eur"]);
        Assert.Equal(10L, usage["known_input_tokens"]!.GetValue<long>()); Assert.Equal(4m, usage["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1, PlanningBenchmarkMeasurements.ExtraTransportCalls(run));
    }
    [Fact]
    public void SelectionRejectsUnknownAndDuplicateCases()
    {
        Assert.Equal(8, PlanningBenchmarkMeasurements.Select(null).Length);
        Assert.Contains("nullable_defaults", PlanningBenchmarkMeasurements.Select(null));
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
    [Theory]
    [InlineData(null)]
    [InlineData("limits")]
    [InlineData("model")]
    [InlineData("missing")]
    [InlineData("regression")]
    [InlineData("calls")]
    [InlineData("retained")]
    public void ParentComparisonRequiresComparableCompleteEvidenceAndEveryAcceptanceCondition(string? defect)
    {
        JsonObject Run(string source, string name, int repetition)
        {
            var row = Row(name, repetition); row["source_commit"] = source;
            row["mode"] = "live"; row["model"] = "pinned-model"; row["provider"] = "provider"; row["campaign"] = "same"; row["usage_bounded"] = true;
            row["calls"] = source == "parent" ? 4 : 2;
            if (source == "parent" && name == "review_french") { row["execution_correct"] = false; row["final_review"] = false; }
            return new() { ["result"] = row, ["session"] = new JsonObject { ["request"] = new JsonObject
            { ["maxModelCalls"] = 8, ["maxReplanAttempts"] = 2, ["generation"] = new JsonObject { ["reasoning"] = "medium", ["maxInputTokensPerRequest"] = 96000, ["maxOutputTokens"] = 32768 } } } };
        }
        var parent = PlanningBenchmarkMeasurements.CandidateCases.SelectMany(n => Enumerable.Range(1, 3).Select(i => Run("parent", n, i))).ToList();
        var candidate = PlanningBenchmarkMeasurements.CandidateCases.SelectMany(n => Enumerable.Range(1, 3).Select(i => Run("candidate", n, i))).ToList();
        switch (defect)
        {
            case "limits": candidate[0]["session"]!["request"]!["maxModelCalls"] = 9; break;
            case "model": candidate[0]["result"]!["model"] = "other-model"; break;
            case "missing": candidate.RemoveAt(0); break;
            case "regression": candidate[0]["result"]!["execution_correct"] = false; break;
            case "calls": foreach (var run in candidate) run["result"]!["calls"] = 4; break;
            case "retained": foreach (var run in candidate.Where(r => r["result"]!["case"]!.ToString() == "review_french"))
                { run["result"]!["execution_correct"] = false; run["result"]!["final_review"] = false; } break;
        }
        var report = PlanningBenchmarkMeasurements.Compare(parent, candidate, "review_french");
        Assert.Equal(defect is null, report["passed"]!.GetValue<bool>());
        if (defect is "missing" or "limits" or "model") Assert.Equal("inconclusive", report["status"]!.ToString());
    }

    private static JsonObject Row(string name, int repetition) => new() { ["source_commit"] = "frozen", ["case"] = name, ["repetition"] = repetition,
        ["calls"] = 1, ["final_review"] = true, ["first_pass_valid"] = true, ["execution_correct"] = true, ["safety_violations"] = new JsonArray() };
}
