using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkPhasePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticHostAdvanceTargetDoesNotTerminateProductiveConstruction(bool diagnostic)
    {
        var request = new PlanningRequest { MaxModelCalls = 1 };
        PlanningBenchmarkMeasurements.ConfigureRequest(request, diagnostic ? ["--diagnostic-optimization-thresholds"] : [], false);
        var state = new PlanningSession { Request = request, Status = PlanningStatus.Generating,
            RequestMeasurements = [new() { RequestId = "recorded:40", Phase = "construction_draft", Admission = "admitted" }] };
        Assert.True(PlanningBenchmarkMeasurements.CanAdvance(state, 39));
        Assert.Equal(diagnostic, PlanningBenchmarkMeasurements.CanAdvance(state, 40));
        if (diagnostic)
        {
            var warning = Assert.Single(state.ThresholdWarnings!);
            Assert.Equal("host_advances", warning.Kind);
            Assert.Equal(41, warning.Current);
            Assert.Equal(40, warning.Threshold);
        }
        else Assert.Null(state.ThresholdWarnings);
        Assert.Null(state.RequestMeasurements[0].Warnings);
        state.Status = PlanningStatus.Stopped;
        Assert.False(PlanningBenchmarkMeasurements.CanAdvance(state, 41));
    }

    [Fact]
    public void DiagnosticRecoveryRecordsElapsedOverrunWithoutRenewingAnInternalDeadline()
    {
        var request = new PlanningRequest();
        request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        PlanningBenchmarkMeasurements.ConfigureRequest(request, ["--max-elapsed-ms", "1000"], false);
        var state = new PlanningSession { Request = request, ActiveMilliseconds = 2000 };
        Assert.Equal(Timeout.InfiniteTimeSpan, PlanningBenchmarkMeasurements.RemainingPlanningTime(state, 2500, 500));
        var warning = Assert.Single(state.ThresholdWarnings!);
        Assert.Equal("elapsed_ms", warning.Kind);
        Assert.Equal(3000, warning.Current);
        Assert.Equal(1000, warning.Threshold);
        var restored = PlanningSessionStorage.Read(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), state.Request.TenantId, state.Request.SessionId);
        Assert.Equal(Timeout.InfiniteTimeSpan, PlanningBenchmarkMeasurements.RemainingPlanningTime(restored, 2500, 500));
        Assert.Single(restored.ThresholdWarnings!);
        Assert.Null(warning.RequestId);
        Assert.Null(state.RequestMeasurements);
        Assert.Equal(2000, restored.ActiveMilliseconds);
    }

    [Fact]
    public void WarningReportJoinsLinkedMirrorsAndKeepsUnboundHostWarningsIndependent()
    {
        var linked = new PlanningThresholdWarning("physical_calls", 4, 1, "captured_optimization_target", "benchmark_http")
            { RequestId = "session:1", SessionId = "session", StepId = "step_1", Checkpoint = 0 };
        var host = new PlanningThresholdWarning("elapsed_ms", 3000, 1000, "retained_host_measurement", "benchmark_host")
            { SessionId = "session", Checkpoint = 0 };
        var state = new PlanningSession
        {
            ThresholdWarnings = [linked, host],
            RequestMeasurements = [new() { RequestId = "session:1", RequestHash = "exact", Phase = "construction_draft",
                StepId = "step_1", Admission = "admitted", Warnings = [new("physical_calls", 4, 1, "host_budget_measurement", "benchmark_http")] }]
        };
        var run = new JsonObject { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) };
        var accounting = new JsonObject { ["planning"] = new JsonObject { ["warnings"] = new JsonArray(new JsonObject
            { ["kind"] = "physical_calls", ["current"] = 4L, ["threshold"] = 1L, ["stage"] = "benchmark_http",
              ["basis"] = "retained_transport_accounting", ["request_id"] = "session:1" }) } };
        var warnings = PlanningBenchmarkMeasurements.RequestMetrics(run, accounting)["warnings"]!.AsArray();
        Assert.Equal(2, warnings.Count);
        var reported = Assert.Single(warnings.OfType<JsonObject>(), warning => warning["request_id"]?.ToString() == "session:1");
        Assert.Equal(3, reported["origins"]!.AsArray().Count);
        Assert.Equal("session", reported["warning"]!["sessionId"]!.ToString());
        var preparation = Assert.Single(warnings.OfType<JsonObject>(), warning => warning["request_id"] is null);
        Assert.Equal("elapsed_ms", preparation["warning"]!["kind"]!.ToString());
        Assert.Equal("session", Assert.Single(preparation["origins"]!.AsArray())!.ToString());
        Assert.Null(state.RequestMeasurements[0].Warnings![0].RequestId);
    }

    [Fact]
    public void PlanningDurationRequiresCaptureAndRecoveryPreservesCumulativeElapsed()
    {
        var missing = new PlanningRequest();
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.PlanningElapsedLimit(missing));
        PlanningBenchmarkMeasurements.ConfigureRequest(missing, [], false);
        Assert.Equal(TimeSpan.FromHours(5), PlanningBenchmarkMeasurements.PlanningElapsedLimit(missing));
        Assert.Equal(3, PlanningTransportPolicies.Read(missing)!.MaxAttempts);
        var frozen = PlanningBenchmarkMeasurements.Configuration(missing);
        Assert.Equal(11, frozen["planning_format"]!.GetValue<int>());
        var restored = PlanningBenchmarkMeasurements.RestoreConfiguration(frozen);
        Assert.True(JsonNode.DeepEquals(frozen, PlanningBenchmarkMeasurements.Configuration(restored)));
        var state = new PlanningSession { Request = restored, ActiveMilliseconds = 90_000 };
        Assert.Equal(TimeSpan.FromHours(5) - TimeSpan.FromSeconds(120),
            PlanningBenchmarkMeasurements.RemainingPlanningTime(state, 110_000, 10_000));
        restored.Options["llm_budget"]!["max_elapsed_ms"] = 180_000;
        Assert.Equal(TimeSpan.FromSeconds(90), PlanningBenchmarkMeasurements.RemainingPlanningTime(state, 60_000));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.RemainingPlanningTime(state, 180_000));
    }

    [Theory]
    [InlineData("construction_select", 6000, 8192)]
    [InlineData("construction_draft", 16000, 8192)]
    [InlineData("construction_local_judge", 8000, 2048)]
    [InlineData("construction_global_judge", 8000, 2048)]
    public void JournalAndPlannerResolveTheSameCapturedRequest(string purpose, int input, int output)
    {
        var planning = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(planning, [], false);
        var policy = Policy(planning);
        var request = PlanningConstructionPhasePolicies.Apply(new LLMRequest
            { StructuredOutputSchema = new JsonObject { ["title"] = purpose } }, planning, purpose);
        Assert.Equal(input, KeyVaultBenchmarkModel.PlanningInputLimit(policy, request));
        Assert.Equal(PlanningConstructionInputPolicy.EffectiveLimit(planning, request), KeyVaultBenchmarkModel.PlanningInputLimit(policy, request));
        Assert.Equal(output, request.MaxTokens);
        KeyVaultBenchmarkModel.RequirePlanningPhasePolicy(policy, request);
        request.MaxTokens = output + 1;
        Assert.Throws<WorkflowRuntimeException>(() => KeyVaultBenchmarkModel.RequirePlanningPhasePolicy(policy, request));
        request.MaxTokens = output; request.Reasoning = "high";
        Assert.Throws<WorkflowRuntimeException>(() => KeyVaultBenchmarkModel.RequirePlanningPhasePolicy(policy, request));
    }

    [Fact]
    public void RetiredAndIncompleteCapturedPoliciesCannotDispatch()
    {
        var request = new LLMRequest { StructuredOutputSchema = new JsonObject { ["title"] = "construction_draft" } };
        Assert.Throws<PlanningConflictException>(() => KeyVaultBenchmarkModel.PlanningInputLimit(new JsonObject { ["planning_format"] = 10 }, request));
        var planning = new PlanningRequest();
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionPhasePolicies.Resolve(planning, "construction_draft"));
    }

    [Fact]
    public void ExternalManifestRetainsTheExactCurrentProfile()
    {
        var planning = LiveWorkflowEvaluation.FreshPlanningRequest([], "fresh-profile", "amazon", "request", "provider", "model");
        var frozen = LiveWorkflowEvaluation.FrozenPlanningConfiguration(planning);
        Assert.True(JsonNode.DeepEquals(planning.Options["construction_limits"], frozen["construction_limits"]));
        Assert.NotNull(frozen["phase_policy_hash"]);
    }

    private static JsonObject Policy(PlanningRequest planning) => new()
    {
        ["planning_format"] = 11, ["max_input_tokens"] = planning.Generation.MaxInputTokensPerRequest,
        ["max_output_tokens"] = planning.Generation.MaxOutputTokens,
        ["construction_limits"] = planning.Options["construction_limits"]!.DeepClone()
    };
}
