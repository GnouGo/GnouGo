using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkPhasePolicyTests
{
    [Theory]
    [InlineData("construction_select", 3000)]
    [InlineData("construction_draft", 8000)]
    [InlineData("construction_local_judge", 4000)]
    public void HistoricalCapturedPolicyKeepsItsOriginalPhaseCeiling(string purpose, int input)
    {
        var policy = new JsonObject { ["construction_version"] = 2, ["max_input_tokens"] = 32000,
            ["max_output_tokens"] = 8192, ["construction_input_limits"] = new JsonObject
            { ["selection"] = 3000, ["draft"] = 8000, ["review"] = 4000 } };
        var request = new LLMRequest { StructuredOutputSchema = new JsonObject { ["title"] = purpose } };
        Assert.Equal(input, KeyVaultBenchmarkModel.PlanningInputLimit(policy, request));
        KeyVaultBenchmarkModel.RequirePlanningPhasePolicy(policy, request);
    }

    [Theory]
    [InlineData("construction_select", 6000, 8192)]
    [InlineData("construction_draft", 16000, 8192)]
    [InlineData("construction_intermediate_judge", 8000, 2048)]
    public void JournalAndPlannerResolveTheSameCapturedFreshRequest(string purpose, int input, int output)
    {
        var planning = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(planning, [], false);
        var policy = new JsonObject
        {
            ["construction_version"] = 2, ["max_input_tokens"] = planning.Generation.MaxInputTokensPerRequest,
            ["max_output_tokens"] = planning.Generation.MaxOutputTokens,
            ["construction_limits"] = planning.Options["construction_limits"]!.DeepClone(),
            ["construction_input_limits"] = planning.Options["construction_input_limits"]!.DeepClone()
        };
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
    public void ExternalManifestRetainsTheExactFreshProfileAndHistoricalAbsence()
    {
        var planning = LiveWorkflowEvaluation.FreshPlanningRequest([], "fresh-profile", "amazon", "request", "provider", "model");
        var frozen = LiveWorkflowEvaluation.FrozenPlanningConfiguration(planning);
        Assert.True(JsonNode.DeepEquals(planning.Options["construction_limits"], frozen["construction_limits"]));
        Assert.NotNull(frozen["phase_policy_hash"]);
        var historical = new PlanningRequest();
        var original = LiveWorkflowEvaluation.FrozenPlanningConfiguration(historical);
        Assert.Null(original["construction_limits"]!["phasePolicy"]);
        Assert.Null(original["phase_policy_hash"]);
    }
}
