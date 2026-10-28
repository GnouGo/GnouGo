using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkPhasePolicyTests
{
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
