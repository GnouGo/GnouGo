using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;
public sealed class BenchmarkMeasurementTests
{
    [Fact]
    public void DeniedReservationHasZeroUsageAndDoesNotCountAsAnHttpAttempt()
    {
        var run = new JsonObject { ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true };
        PlanningBenchmarkMeasurements.RecordUsage(run, "dispatched", new()
        { ["input_tokens"] = 10, ["output_tokens"] = 2, ["benchmark_cost_eur"] = .1m, ["transport_attempts"] = 8 });
        PlanningBenchmarkMeasurements.RecordUsage(run, "denied", new()
        { ["input_tokens"] = 0, ["output_tokens"] = 0, ["benchmark_cost_eur"] = 0m, ["transport_attempts"] = 0 });
        Assert.Equal(8, 2 + PlanningBenchmarkMeasurements.ExtraTransportCalls(run));
        var usage = PlanningBenchmarkMeasurements.Usage(run, true);
        Assert.True(usage["usage_complete"]!.GetValue<bool>()); Assert.True(usage["usage_bounded"]!.GetValue<bool>());
        Assert.Equal(.1m, usage["known_cost_eur"]!.GetValue<decimal>());
    }
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
    [InlineData("reference_regression")]
    [InlineData("reference_missing")]
    [InlineData("reference_usage")]
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
        var reference = PlanningBenchmarkMeasurements.CandidateCases.SelectMany(n => Enumerable.Range(1, 3).Select(i => Run("reference", n, i))).ToList();
        switch (defect)
        {
            case "reference_regression":
                var failed = candidate.First(r => r["result"]!["case"]!.ToString() == "review_french");
                failed["result"]!["execution_correct"] = false; failed["result"]!["final_review"] = false; break;
            case "reference_missing": reference.RemoveAt(0); break;
            case "reference_usage": reference[0]["result"]!["usage_bounded"] = false; break;
            case "limits": candidate[0]["session"]!["request"]!["maxModelCalls"] = 9; break;
            case "model": candidate[0]["result"]!["model"] = "other-model"; break;
            case "missing": candidate.RemoveAt(0); break;
            case "regression": candidate[0]["result"]!["execution_correct"] = false; break;
            case "calls": foreach (var run in candidate) run["result"]!["calls"] = 4; break;
            case "retained": foreach (var run in candidate.Where(r => r["result"]!["case"]!.ToString() == "review_french"))
                { run["result"]!["execution_correct"] = false; run["result"]!["final_review"] = false; } break;
        }
        var report = PlanningBenchmarkMeasurements.Compare(parent, candidate, "review_french", reference);
        Assert.Equal(defect is null, report["passed"]!.GetValue<bool>());
        if (defect == "reference_regression") { Assert.True(report["no_case_regression"]!.GetValue<bool>()); Assert.False(report["no_reference_case_regression"]!.GetValue<bool>()); }
        if (defect is "missing" or "limits" or "model" or "reference_missing" or "reference_usage") Assert.Equal("inconclusive", report["status"]!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("unsafe")]
    [InlineData("best_regression")]
    [InlineData("unbounded")]
    [InlineData("limits")]
    public void SemanticCandidateUsesBestPerCaseBaselineAndDoesNotAddEfficiencyGates(string? defect)
    {
        var names = new[] { "conditional", "review_french", "review_distractors" };
        List<JsonObject> Cohort(string source) => names.SelectMany(name => Enumerable.Range(1, 3).Select(i =>
        {
            var row = Row(name, i); row["source_commit"] = source; row["provider"] = "same"; row["model"] = "same"; row["campaign"] = "same";
            row["mode"] = "live"; row["usage_bounded"] = true; row["usage_complete"] = true; row["calls"] = source == "candidate" ? 6 : 2;
            if (name == "review_distractors" && i == 3 || name == "review_french" && source != "stabilization" && source != "candidate")
            { row["execution_correct"] = false; row["final_review"] = false; }
            return new JsonObject { ["result"] = row, ["session"] = new JsonObject { ["request"] = new JsonObject
                { ["maxModelCalls"] = 8, ["maxReplanAttempts"] = 2, ["generation"] = new JsonObject { ["reasoning"] = "medium", ["maxInputTokensPerRequest"] = 96000, ["maxOutputTokens"] = 32768 } } } };
        })).ToList();
        var parent = Cohort("parent"); var previous = Cohort("previous"); var stabilization = Cohort("stabilization"); var candidate = Cohort("candidate");
        if (defect == "missing") candidate.RemoveAt(0);
        if (defect == "unsafe") candidate[0]["result"]!["safety_violations"]!.AsArray().Add("unsafe");
        if (defect == "best_regression") { candidate[3]["result"]!["execution_correct"] = false; candidate[3]["result"]!["final_review"] = false; }
        if (defect == "unbounded") candidate[0]["result"]!["usage_bounded"] = false;
        if (defect == "limits") candidate[0]["session"]!["request"]!["maxModelCalls"] = 9;
        var report = PlanningBenchmarkMeasurements.CompareBest(names, new Dictionary<string, IReadOnlyList<JsonObject>> { ["parent"] = parent, ["previous"] = previous, ["stabilization"] = stabilization }, candidate);
        Assert.Equal(defect is null, report["passed"]!.GetValue<bool>());
        if (defect is "missing" or "unbounded" or "limits") Assert.Equal("inconclusive", report["status"]!.ToString());
        if (defect is null) Assert.Equal(6, report["metrics"]!["candidate"]!["median_calls"]!.GetValue<double>());
    }

    [Fact]
    public void ClosedUnknownHttpAttemptsStayReservedAndNeverBecomeKnownTokens()
    {
        var journal = new JsonObject { ["input_ceiling"] = 96000L, ["output_ceiling"] = 32768L, ["cost_ceiling_eur"] = 3m,
            ["transport"] = new JsonObject { ["Attempts"] = new JsonArray(new JsonObject { ["Status"] = null }, new JsonObject { ["Status"] = 429 }) } };
        var original = journal.ToJsonString();
        var usage = PlanningBenchmarkMeasurements.ClosedHttpUsage([journal], 2)!;
        Assert.True(usage["usage_bounded"]!.GetValue<bool>()); Assert.False(usage["usage_complete"]!.GetValue<bool>());
        Assert.Null(usage["input_tokens"]); Assert.Null(usage["estimated_cost_eur"]); Assert.Equal(3m, usage["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(0L, usage["known_input_tokens"]!.GetValue<long>()); Assert.Equal(original, journal.ToJsonString());
        Assert.Null(PlanningBenchmarkMeasurements.ClosedHttpUsage([journal], 3));
        journal.Remove("cost_ceiling_eur"); Assert.Null(PlanningBenchmarkMeasurements.ClosedHttpUsage([journal], 2));
    }

    private static JsonObject Row(string name, int repetition) => new() { ["source_commit"] = "frozen", ["case"] = name, ["repetition"] = repetition,
        ["calls"] = 1, ["final_review"] = true, ["first_pass_valid"] = true, ["execution_correct"] = true, ["safety_violations"] = new JsonArray() };
}
