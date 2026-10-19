using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;
public sealed class BenchmarkMeasurementTests
{
    [Fact]
    public void JudgeMetricsUseIndependentLabelsAndKeepUnknownCandidatesUnknown()
    {
        var run = new JsonObject { ["candidate_reviews"] = new JsonArray(
            new JsonObject { ["business_correct"] = true, ["judge_findings"] = 1 },
            new JsonObject { ["business_correct"] = false, ["judge_findings"] = 0 },
            new JsonObject { ["business_correct"] = false, ["judge_findings"] = 1 },
            new JsonObject { ["business_correct"] = null, ["judge_findings"] = 0 }) };
        var metrics = PlanningBenchmarkMeasurements.JudgeMetrics(run);
        Assert.Equal(1, metrics["false_positives"]!.GetValue<int>());
        Assert.Equal(1, metrics["false_negatives"]!.GetValue<int>());
        Assert.Equal(1, metrics["true_positives"]!.GetValue<int>());
        Assert.Equal(1, metrics["unknown_candidates"]!.GetValue<int>());
        Assert.Null(metrics["converged"]);
    }

    [Fact]
    public void OracleLabelDoesNotDependOnJudgeChangingTheApprovalHash()
    {
        var run = new JsonObject { ["diagnostic_history"] = new JsonArray() };
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Graph = PlannerFixture.Greeting();
        state.Requirements = PlannerFixture.Requirements(); state.Yaml = "retained";
        PlanningBenchmarkMeasurements.Capture(run, state);
        var oldHash = state.ComputeArtifactHash(); state.Request.Options["business_judge_reviews"] = new JsonArray();
        Assert.NotEqual(oldHash, state.ComputeArtifactHash());
        PlanningBenchmarkMeasurements.RecordBusinessOracle(run, state, true, "independent-fixture-v1");
        Assert.True(run["candidate_reviews"]![0]!["business_correct"]!.GetValue<bool>());
        Assert.Equal("independent-fixture-v1", run["candidate_reviews"]![0]!["oracle_version"]!.ToString());
    }

    [Fact]
    public void ReturningToAnEarlierCandidateUsesItsOracleAndRetainsTheRegression()
    {
        var run = new JsonObject { ["diagnostic_history"] = new JsonArray() };
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Graph = PlannerFixture.Greeting();
        state.Requirements = PlannerFixture.Requirements();
        PlanningBenchmarkMeasurements.Capture(run, state);
        PlanningBenchmarkMeasurements.RecordBusinessOracle(run, state, false, "independent-fixture-v1");
        var original = state.Plan;
        state.Plan = PlanningCorpus.Greeting(); state.Plan.Root.Tasks[0].Objective = "A corrected request outcome";
        PlanningBenchmarkMeasurements.Capture(run, state);
        PlanningBenchmarkMeasurements.RecordBusinessOracle(run, state, true, "independent-fixture-v1");
        state.Plan = original;
        PlanningBenchmarkMeasurements.Capture(run, state);
        var metrics = PlanningBenchmarkMeasurements.JudgeMetrics(run);
        Assert.Equal(2, metrics["candidates"]!.GetValue<int>());
        Assert.False(metrics["converged"]!.GetValue<bool>());
        Assert.True(metrics["introduced_regression"]!.GetValue<bool>());
    }

    [Fact]
    public void InconclusiveJudgeCannotKeepAnEarlierZeroFindingCount()
    {
        var run = new JsonObject { ["diagnostic_history"] = new JsonArray() };
        var state = PlannerFixture.Session(); state.Plan = PlanningCorpus.Greeting(); state.Graph = PlannerFixture.Greeting();
        state.ValidationResults = [new("judge:summary", "unverified", "Advisory", [])];
        PlanningBenchmarkMeasurements.Capture(run, state);
        state.ValidationResults = [new("judge:summary", "inconclusive", "Unusable response", [])];
        PlanningBenchmarkMeasurements.Capture(run, state);
        var metrics = PlanningBenchmarkMeasurements.JudgeMetrics(run);
        Assert.Equal(1, metrics["unjudged_candidates"]!.GetValue<int>());
        Assert.Equal(0, metrics["true_negatives"]!.GetValue<int>());
    }

    [Fact]
    public void StageReceiptReplayCountsOneLogicalCallAndPreservesUnknownPhysicalUsage()
    {
        var run = new JsonObject { ["usage_receipts"] = new JsonObject(), ["usage_complete"] = true };
        PlanningBenchmarkMeasurements.RecordCall(run, "judge-request", "judge", 2);
        PlanningBenchmarkMeasurements.RecordCall(run, "judge-request", "judge", 3);
        PlanningBenchmarkMeasurements.RecordUsage(run, "judge-request", null);
        var result = new JsonObject { ["mode"] = "live" };
        PlanningBenchmarkMeasurements.ProposalMetrics(run, result);
        var stage = result["planning_stages"]!["judge"]!;
        Assert.Equal(1, stage["logical_calls"]!.GetValue<int>());
        Assert.Equal(5, stage["elapsed_ms"]!.GetValue<double>());
        Assert.Null(stage["physical_attempts"]); Assert.Null(stage["input_tokens"]); Assert.Null(stage["estimated_cost_eur"]);
    }

    [Fact]
    public void BenchmarkCreationDefaultsAndExplicitOverridesAreSeparateFromHistoricalResume()
    {
        var fresh = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(fresh, [], false);
        Assert.Equal(64, fresh.MaxModelCalls); Assert.Equal(4, fresh.MaxReplanAttempts);
        Assert.Equal(32000, fresh.Generation.MaxInputTokensPerRequest); Assert.Equal(8192, fresh.Generation.MaxOutputTokens);
        PlanningBenchmarkMeasurements.ConfigureRequest(fresh, ["--max-model-calls", "9", "--max-repairs", "0", "--max-input-tokens", "24000", "--max-output-tokens", "8192", "--business-judge"], false);
        Assert.Equal(9, fresh.MaxModelCalls); Assert.Equal(0, fresh.MaxReplanAttempts);
        Assert.Equal(24000, fresh.Generation.MaxInputTokensPerRequest); Assert.Equal(8192, fresh.Generation.MaxOutputTokens);
        Assert.True(fresh.Options["business_judge"]!.GetValue<bool>());
        var historical = System.Text.Json.JsonSerializer.Deserialize("{}", PlanningJsonContext.Default.PlanningRequest)!;
        var original = System.Text.Json.JsonSerializer.Serialize(historical, PlanningJsonContext.Default.PlanningRequest);
        PlanningBenchmarkMeasurements.ConfigureRequest(historical, [], true);
        PlanningBenchmarkMeasurements.ConfigureRequest(historical, ["--max-model-calls", "8", "--max-repairs", "2", "--no-business-judge"], true);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(historical, PlanningJsonContext.Default.PlanningRequest));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(historical, ["--max-model-calls", "12"], true));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(historical, ["--business-judge"], true));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(fresh, ["--no-business-judge"], true));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(fresh, ["--max-input-tokens", "0"], false));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(fresh, ["--business-judge", "--no-business-judge"], false));
    }

    [Fact]
    public void ConstructionCapacityAndBudgetsRoundTripWithoutMigratingHistoricalConfigurations()
    {
        var historical = System.Text.Json.JsonSerializer.Deserialize("{}", PlanningJsonContext.Default.PlanningRequest)!;
        var oldConfiguration = PlanningBenchmarkMeasurements.Configuration(historical);
        Assert.Null(oldConfiguration["construction_version"]);
        Assert.True(JsonNode.DeepEquals(oldConfiguration,
            PlanningBenchmarkMeasurements.Configuration(PlanningBenchmarkMeasurements.RestoreConfiguration(oldConfiguration))));
        var fresh = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(fresh, ["--construction-preset", "large", "--max-total-tokens", "1000000", "--max-elapsed-ms", "600000", "--automatic-correction"], false);
        var configuration = PlanningBenchmarkMeasurements.Configuration(fresh);
        var restored = PlanningBenchmarkMeasurements.RestoreConfiguration(configuration);
        PlanningBenchmarkMeasurements.ConfigureRequest(restored, [], true);
        Assert.True(JsonNode.DeepEquals(configuration, PlanningBenchmarkMeasurements.Configuration(restored)));
        Assert.Equal(384, restored.MaxModelCalls); Assert.Equal(64, restored.MaxReplanAttempts);
        Assert.Equal(5000, restored.Policy.MaxStepsPerWorkflow); Assert.Equal(1024, restored.Policy.MaxWorkflows);
        Assert.True(restored.Policy.RequireExternalConfirmation);
        Assert.Equal(1000000, restored.Options["llm_budget"]!["max_total_tokens"]!.GetValue<int>());
        Assert.Equal(600000, restored.Options["llm_budget"]!["max_elapsed_ms"]!.GetValue<int>());
        restored.MaxModelCalls = 96; restored.MaxReplanAttempts = 24;
        PlanningBenchmarkMeasurements.ConfigureRequest(restored, ["--construction-preset", "large"], false);
        Assert.Equal(96, restored.MaxModelCalls); Assert.Equal(24, restored.MaxReplanAttempts);
        var session = new PlanningSession { Request = restored };
        PlanningBenchmarkMeasurements.CaptureConstructionSettings(session);
        Assert.Equal(8192, session.Construction!.Limits.DraftOutputTokens); Assert.Equal(2048, session.Construction.Limits.ReviewOutputTokens);
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(restored, ["--construction-preset", "ordinary"], true));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(restored, ["--max-total-tokens", "15000000"], true));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(historical, ["--construction-preset", "ordinary"], true));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(new(), ["--max-elapsed-ms", "18000001"], false));
    }

    [Theory]
    [InlineData(null, "measured")]
    [InlineData("budget", "inconclusive")]
    [InlineData("unknown", "inconclusive")]
    [InlineData("missing", "inconclusive")]
    [InlineData("source", "inconclusive")]
    [InlineData("fixture", "fixture_only")]
    public void JudgeComparisonIsolatesJudgeFromBudgetsAndRequiresIndependentOutcomes(string? defect, string expected)
    {
        JsonObject[] Arm(bool judge) => Enumerable.Range(1, 3).Select(i => new JsonObject {
            ["case"] = "local", ["repetition"] = i, ["source_commit"] = "same-code", ["mode"] = "live",
            ["comparison_manifest"] = "frozen-context-oracle", ["provider"] = "same", ["model"] = "same", ["campaign"] = "same",
            ["configuration"] = new JsonObject { ["calls"] = 12, ["repairs"] = 4, ["input_tokens"] = 32000, ["output_tokens"] = 32768, ["reasoning"] = "medium", ["business_judge"] = judge },
            ["business_review"] = new JsonObject { ["converged"] = true, ["false_positives"] = 0, ["false_negatives"] = 0 },
            ["calls"] = judge ? 2 : 1, ["input_tokens"] = 100, ["output_tokens"] = 10, ["estimated_cost_eur"] = .1m,
            ["elapsed_ms"] = 10, ["safety_violations"] = new JsonArray() }).ToArray();
        var before = Arm(false); var after = Arm(true);
        if (defect == "budget") after[0]["configuration"]!["calls"] = 8;
        if (defect == "unknown") after[0]["business_review"]!["converged"] = null;
        if (defect == "missing") after = after[..2];
        if (defect == "source") after[0]["source_commit"] = "different-code";
        if (defect == "fixture") foreach (var row in before.Concat(after)) row["mode"] = "fixture";
        var comparison = PlanningBenchmarkMeasurements.CompareArms(before, after, ["local"], "judge");
        Assert.Equal(expected, comparison["status"]!.ToString());
        Assert.Equal("judge", comparison["variable"]!.ToString());
        Assert.False(comparison["live_benefit_established"]!.GetValue<bool>());
    }

    [Fact]
    public void DefaultsComparisonKeepsJudgeOffAndUnknownCostsOutOfMedians()
    {
        JsonObject[] Arm(int calls) => Enumerable.Range(1, 3).Select(i => new JsonObject {
            ["case"] = "local", ["repetition"] = i, ["source_commit"] = "same-code", ["mode"] = "live",
            ["comparison_manifest"] = "frozen", ["provider"] = "same", ["model"] = "same", ["campaign"] = "same",
            ["configuration"] = PlanningBenchmarkMeasurements.Configuration(new PlanningRequest { MaxModelCalls = calls }),
            ["business_review"] = new JsonObject { ["converged"] = true }, ["estimated_cost_eur"] = .1m,
            ["safety_violations"] = new JsonArray() }).ToArray();
        var before = Arm(8); var after = Arm(12); after[0]["estimated_cost_eur"] = null;
        var comparison = PlanningBenchmarkMeasurements.CompareArms(before, after, ["local"], "defaults");
        Assert.Equal("measured", comparison["status"]!.ToString());
        Assert.Null(comparison["candidate"]!["estimated_cost_eur"]!["median"]);
        Assert.Equal(2, comparison["candidate"]!["estimated_cost_eur"]!["known_runs"]!.GetValue<int>());
        foreach (var row in after) row["configuration"]!["business_judge"] = true;
        Assert.Equal("inconclusive", PlanningBenchmarkMeasurements.CompareArms(before, after, ["local"], "defaults")["status"]!.ToString());
    }

    [Theory]
    [InlineData("improved", "passed")]
    [InlineData("zero", "inconclusive")]
    [InlineData("unknown", "inconclusive")]
    [InlineData("different", "inconclusive")]
    [InlineData("tokens", "failed")]
    [InlineData("safety", "failed")]
    public void FirstProposalGateRequiresComparableBusinessOraclesAndKnownUsage(string variant, string status)
    {
        JsonObject[] Cohort(string source) => PlanningBenchmarkMeasurements.CandidateCases.SelectMany(name => Enumerable.Range(1, 3).Select(i => new JsonObject
        {
            ["case"] = name, ["repetition"] = i, ["source_commit"] = source, ["mode"] = "live", ["manifest"] = "frozen", ["provider"] = "provider", ["model"] = "fixed", ["campaign"] = "same",
            ["first_proposal_business_correct"] = true, ["calls"] = 2, ["input_tokens"] = 100L, ["output_tokens"] = 20L, ["safety_violations"] = new JsonArray()
        })).ToArray();
        var before = Cohort("before"); var after = Cohort("after");
        if (variant != "zero") { foreach (var row in before.Take(4)) row["first_proposal_business_correct"] = false; foreach (var row in after.Take(3)) row["first_proposal_business_correct"] = false; }
        if (variant == "unknown") after[0]["first_proposal_business_correct"] = null;
        if (variant == "different") after[0]["manifest"] = "changed";
        if (variant == "tokens") foreach (var row in after) row["input_tokens"] = 200L;
        if (variant == "safety") after[0]["safety_violations"]!.AsArray().Add("extra effect");
        var comparison = PlanningBenchmarkMeasurements.CompareFirstProposals(before, after, PlanningBenchmarkMeasurements.CandidateCases);
        Assert.Equal(status, comparison["status"]!.ToString());
        Assert.Equal(variant is "zero" or "unknown" ? null : .25, comparison["relative_failure_reduction"]?.GetValue<double>());
    }
    [Fact]
    public void FirstProposalIsIndependentOfDiscoveryCallsAndRetainsItsOwnFailures()
    {
        var run = new JsonObject { ["diagnostic_history"] = new JsonArray() };
        PlanningBenchmarkMeasurements.RecordResponse(run, new() { ClientRequestId = "discover" }, new() { Json = new JsonObject { ["discoveryRequests"] = new JsonArray() } }, 1);
        Assert.Null(run["first_proposal"]);
        PlanningBenchmarkMeasurements.RecordResponse(run, new() { ClientRequestId = "proposal" }, new() { Json = new JsonObject { ["plan"] = new JsonObject() } }, 2);
        var state = new PlanningSession { ModelCalls = 2, Status = PlanningStatus.Stopped, Diagnostics = [new("TASK_INPUT_REQUIRED", "/tasks/a/inputs/x", "Missing input")] };
        PlanningBenchmarkMeasurements.Capture(run, state);
        state.ModelCalls = 3; state.Status = PlanningStatus.FinalReview; state.Diagnostics.Clear();
        PlanningBenchmarkMeasurements.RecordResponse(run, new() { ClientRequestId = "correction" }, new() { Json = new JsonObject { ["plan"] = new JsonObject { ["corrected"] = true } } }, 3);
        PlanningBenchmarkMeasurements.Capture(run, state);
        Assert.Equal(2, run["first_proposal"]!["call"]!.GetValue<int>());
        Assert.False(run["first_proposal"]!["structurally_valid"]!.GetValue<bool>());
        Assert.Single(run["first_proposal"]!["diagnostics"]!.AsArray());
        Assert.Null(run["first_proposal"]!["business_correct"]);
        Assert.Null(run["first_proposal"]!["response"]!["plan"]!["corrected"]);
    }
    [Fact]
    public void MalformedJudgeResponseCannotBecomeTheFirstProposal()
    {
        var run = new JsonObject();
        PlanningBenchmarkMeasurements.RecordResponse(run, new() { ClientRequestId = "judge" }, new() { Json = new JsonObject { ["plan"] = new JsonObject() } }, 1, "judge");
        Assert.Null(run["first_proposal"]);
    }
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
