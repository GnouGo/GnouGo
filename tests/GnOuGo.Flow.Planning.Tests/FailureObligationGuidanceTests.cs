using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FailureObligationGuidanceTests
{
    [Theory]
    [InlineData("select")]
    [InlineData("implementation")]
    [InlineData("discovery")]
    [InlineData("draft")]
    [InlineData("local_review")]
    [InlineData("global_review")]
    public async Task DecisionsDistinguishFailureTruthfulnessFromRecoveryWithoutChangingAuthority(string phase)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var unissued = phase switch
        {
            "select" => PlanningCompactConstruction.SelectionRequest(state),
            "implementation" => PlanningCompactConstruction.ImplementationRequest(state),
            "discovery" => PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
                PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose),
            "draft" => PlanningConstruction.DraftRequest(state, repair: false),
            _ => HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: phase == "global_review").Request
        };
        var purpose = phase switch
        {
            "select" => PlanningCompactConstruction.SelectPurpose,
            "implementation" => PlanningCompactConstruction.ImplementationPurpose,
            "discovery" => PlanningCompactConstruction.DiscoveryPurpose,
            "draft" => PlanningConstruction.DraftPurpose,
            "local_review" => PlanningConstruction.LocalPurpose,
            _ => PlanningConstruction.GlobalPurpose
        };
        var request = PlanningModelCalls.PrepareRequest(state, purpose, unissued.Prompt, unissued.StructuredOutputSchema!.AsObject());
        Assert.Contains("Do not infer mandatory recovery or partial delivery from truthfulness or cleanup alone", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Preserve explicitly requested recovery", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("raised operation failure", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        if (phase.EndsWith("review", StringComparison.Ordinal))
        {
            var schema = request.StructuredOutputSchema!.ToJsonString();
            Assert.Contains("needs_change", schema, StringComparison.Ordinal);
            Assert.Contains("needs_context", schema, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Keep errors explicit and always release the resource.")]
    [InlineData("After a failed item, continue with every remaining item and save a partial report; always release the resource.")]
    public async Task BothFailureContractsRemainVerbatimAtSelectionAndReview(string original)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        var compact = state.Construction!.Compact!;
        compact.Sources.Add(new() { Id = "failure_contract", Kind = "global", Indexed = true, Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original) });
        compact.GlobalSourceIds.Add("failure_contract");
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var request in new[] { PlanningCompactConstruction.SelectionRequest(state),
                     PlanningCompactConstruction.ImplementationRequest(state),
                     HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: true).Request })
        {
            Assert.Contains(original, request.Prompt, StringComparison.Ordinal);
            var issued = PlanningModelCalls.PrepareRequest(state, request.StructuredOutputSchema!["title"]!.ToString(),
                request.Prompt, request.StructuredOutputSchema.AsObject());
            Assert.Contains("Preserve explicitly requested recovery", issued.Prompt, StringComparison.Ordinal);
            Assert.Contains(original, issued.Prompt, StringComparison.Ordinal);
        }
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task StrictReviewKeepsOriginalContextAndVerdictChoicesWhenGuidanceWouldExceedItsCapturedTarget()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        const string prompt = "Assess actual behavior.\n{\"original\":\"Continue after each failed item and save partial results.\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{"verdict":{"type":"string","enum":["ok","needs_change","needs_context"]}},"required":["verdict"],"additionalProperties":false}""")!.AsObject();
        var original = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.GlobalPurpose, prompt, schema);
        Assert.Contains("Do not infer mandatory recovery", original.Prompt, StringComparison.Ordinal);
        var bare = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        bare.Prompt = prompt;
        var limit = checked((int)ModelRequestAdmission.EstimateTokens(bare) + 1);
        var policy = state.Request.Options["construction_limits"]!["phasePolicy"]!["review"]!;
        policy["targetInputTokens"] = limit;
        policy["maxInputTokens"] = limit;
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.GlobalPurpose, prompt, schema);
        Assert.Equal(prompt, issued.Prompt);
        Assert.True(JsonNode.DeepEquals(original.StructuredOutputSchema, issued.StructuredOutputSchema));
        Assert.InRange(ModelRequestAdmission.EstimateTokens(issued), 1, limit);
        Assert.NotEqual(original.ClientRequestId, issued.ClientRequestId);
        Assert.Equal(issued.ClientRequestId, PlanningModelCalls.PrepareRequest(state, PlanningConstruction.GlobalPurpose, prompt, schema).ClientRequestId);
        Assert.Null(state.PendingCall);
        Assert.Equal(0, state.ModelCalls);
    }
}
