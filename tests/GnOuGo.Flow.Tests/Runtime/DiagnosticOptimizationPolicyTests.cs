using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DiagnosticOptimizationPolicyTests
{
    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("{\"token_threshold_policy\":\"advisory\"}", true, false)]
    [InlineData("{\"optimization_threshold_policy\":\"diagnostic-v1\"}", true, true)]
    [InlineData("{\"token_threshold_policy\":\"advisory\",\"optimization_threshold_policy\":\"diagnostic-v1\"}", true, true)]
    public void CapturedAuthorityDoesNotBroadenExistingTokenOnlySessions(string json, bool tokens, bool optimization)
    {
        var request = new PlanningRequest { Options = JsonNode.Parse(json)!.AsObject() };
        var exact = request.Options.ToJsonString();
        Assert.Equal(tokens, PlanningTokenThresholds.Advisory(request));
        Assert.Equal(optimization, PlanningTokenThresholds.OptimizationAdvisory(request));
        Assert.Equal(exact, request.Options.ToJsonString());
    }

    [Theory]
    [InlineData("{\"token_threshold_policy\":\"wrong\",\"optimization_threshold_policy\":\"diagnostic-v1\"}")]
    [InlineData("{\"token_threshold_policy\":\"advisory\",\"optimization_threshold_policy\":\"wrong\"}")]
    [InlineData("{\"optimization_threshold_policy\":null}")]
    [InlineData("{\"optimization_threshold_policy\":true}")]
    [InlineData("{\"optimization_threshold_policy\":{}}")]
    public void InvalidCapturedModeCannotHideBehindAnotherAdvisoryField(string json)
    {
        var options = JsonNode.Parse(json)!.AsObject();
        Assert.Throws<PlanningConflictException>(() => PlanningTokenThresholds.Advisory(options));
        Assert.Throws<PlanningConflictException>(() => PlanningTokenThresholds.OptimizationAdvisory(options));
    }

    [Theory]
    [InlineData("token_threshold_policy")]
    [InlineData("optimization_threshold_policy")]
    public void PublicHostsRejectEitherReservedAuthorityEvenWhenNull(string field)
        => Assert.Throws<PlanningConflictException>(() => PlanningTokenThresholds.RejectReserved(new() { [field] = null }));

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"token_threshold_policy\":\"advisory\"}", false)]
    [InlineData("{\"optimization_threshold_policy\":\"diagnostic-v1\"}", true)]
    public void MandatoryReviewHeadroomCanExceedOnlyDiagnosticCallTargets(string json, bool diagnostic)
    {
        var planning = new PlanningRequest { MaxModelCalls = 1, Options = JsonNode.Parse(json)!.AsObject() };
        planning.Options["llm_budget"] = new JsonObject { ["max_calls"] = 1 };
        PlanningTransportPolicies.Capture(planning);
        var request = new LLMRequest { RequiredReviewCalls = 3 };
        if (diagnostic) PlanningTransportPolicies.ValidateIssued(request, planning);
        else Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, planning));
        Assert.Equal(3, request.RequiredReviewCalls);
        request.RequiredReviewCalls = -1;
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, planning));
        request.RequiredReviewCalls = null;
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, planning));
    }
}
