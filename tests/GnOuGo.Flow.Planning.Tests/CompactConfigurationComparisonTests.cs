using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConfigurationComparisonTests
{
    [Fact]
    public void HistoricalSafeRowsWithoutJudgeSwitchKeepTheirMatchedComparison()
    {
        var before = Arm(false); var after = Arm(true);
        var settings = (JsonObject)before[0]["configuration"]!.DeepClone();
        settings.Remove("business_judge");
        foreach (var row in before.Concat(after)) row["configuration"] = settings.DeepClone();
        var report = PlanningBenchmarkMeasurements.CompareCompactConstruction(before, after, ["collections", "amazon", "code"]);
        Assert.Equal("passed", report["status"]!.ToString());
        Assert.True(report["matched_cumulative_and_output_allowances"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("none", "passed")]
    [InlineData("undeclared", "inconclusive")]
    [InlineData("within_arm", "inconclusive")]
    [InlineData("calls", "inconclusive")]
    [InlineData("safety", "inconclusive")]
    [InlineData("financial", "inconclusive")]
    [InlineData("structural", "inconclusive")]
    [InlineData("manifest", "inconclusive")]
    [InlineData("unknown_outcome", "inconclusive")]
    public void ConfigurationComparisonRequiresDeclaredFrozenArmsAndSharedSafety(string defect, string expected)
    {
        var before = Arm(false); var after = Arm(true);
        var policy = new JsonObject
        {
            ["version"] = 2, ["mode"] = "validated-configurations", ["comparison_manifest"] = "frozen-shared-context",
            ["baseline_configuration"] = before[0]["configuration"]!.DeepClone(),
            ["candidate_configuration"] = after[0]["configuration"]!.DeepClone()
        };
        if (defect == "within_arm") after[0]["configuration"]!["reasoning"] = "high";
        if (defect is "calls" or "safety" or "financial" or "structural")
        {
            foreach (var row in after)
                if (defect == "calls") row["configuration"]!["calls"] = 128;
                else if (defect == "safety") row["configuration"]!["policy"]!["requireExternalConfirmation"] = false;
                else if (defect == "financial") row["configuration"]!["llm_budget"]!["max_total_tokens"] = 16000000;
                else row["configuration"]!["construction_limits"]!["maxTasksPerDraft"] = 100;
            policy["candidate_configuration"] = after[0]["configuration"]!.DeepClone();
        }
        if (defect == "manifest") policy["comparison_manifest"] = "other";
        if (defect == "unknown_outcome") after[0]["independent_business_success"] = null;
        var report = PlanningBenchmarkMeasurements.CompareCompactConstruction(before, after, ["collections", "amazon", "code"],
            defect == "undeclared" ? null : policy);
        Assert.Equal(expected, report["status"]!.ToString());
        Assert.False(report["matched_cumulative_and_output_allowances"]!.GetValue<bool>());
        if (defect == "none")
        {
            Assert.True(report["frozen_settings_within_each_arm"]!.GetValue<bool>());
            Assert.True(report["shared_cumulative_and_safety_allowances"]!.GetValue<bool>());
            Assert.True(report["declared_configurations_match"]!.GetValue<bool>());
            Assert.Contains(report["intentional_setting_differences"]!.AsArray(), d => d!["setting"]!.ToString() == "reasoning");
            Assert.Equal(9m, report["candidate"]!["estimated_cost_eur"]!["total"]!.GetValue<decimal>());
            Assert.Contains("not an architecture-only causal experiment", report["interpretation"]!.ToString());
        }
    }

    private static JsonObject[] Arm(bool compact)
    {
        var request = new PlanningRequest(); PlanningBenchmarkMeasurements.ConfigureRequest(request, [], false);
        request.Generation.Reasoning = compact ? "low" : "medium";
        if (!compact)
        {
            request.Options["construction_version"] = 1;
            request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            request.Options["construction_limits"]!.AsObject().Remove("inputLimits");
            request.Options.Remove("construction_input_limits");
        }
        else request.Options["construction_limits"]!["phasePolicy"]!["navigation"]!["reasoning"] = "low";
        var configuration = PlanningBenchmarkMeasurements.Configuration(request);
        return new[] { "collections", "amazon", "code" }.SelectMany(name => Enumerable.Range(1, 3).Select(repetition =>
        {
            var measurement = PlanningRequestMeasurements.Measure(new LLMRequest { Prompt = new string('a', compact ? 1000 : 5000) },
                "construction_draft", compact ? 16000 : 32000);
            measurement.Admission = "admitted";
            return new JsonObject
            {
                ["case"] = name, ["repetition"] = repetition, ["source_commit"] = compact ? "candidate" : "baseline",
                ["mode"] = "live", ["comparison_manifest"] = "frozen-shared-context", ["provider"] = "provider", ["model"] = "model",
                ["campaign"] = "campaign", ["configuration"] = configuration.DeepClone(), ["independent_business_success"] = compact,
                ["safety_violations"] = new JsonArray(), ["calls"] = compact ? 20 : 2, ["input_tokens"] = 1000, ["output_tokens"] = 100,
                ["estimated_cost_eur"] = 1m, ["elapsed_ms"] = 500,
                ["request_metrics"] = new JsonObject { ["requests"] = new JsonArray(
                    JsonSerializer.SerializeToNode(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement)) }
            };
        })).ToArray();
    }
}
