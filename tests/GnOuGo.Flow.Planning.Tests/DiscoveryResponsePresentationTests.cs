using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiscoveryResponsePresentationTests
{
    private const string Prompt = "Inspect the exact available contracts.\n{\"original\":\"Keep this context intact.\"}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactFieldGuideKeepsTheExactGrammarAuthoritativeWithoutCopyingIt(bool diagnostic)
    {
        var state = Session(diagnostic);
        var phase = Navigation(state);
        phase["targetInputTokens"] = diagnostic ? 2000 : 12000;
        phase["maxInputTokens"] = 12000;
        var schema = Schema();
        var original = schema.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, Prompt, schema);
        Assert.Contains("Discovery response field guide", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("response: {answer}", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(request.StructuredOutputSchema!.ToJsonString(), request.Prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n{\"original\":\"Keep this context intact.\"}", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(original, schema.ToJsonString());
        Assert.True(request.StructuredOutputStrict);
        Assert.InRange(ModelRequestAdmission.EstimateTokens(request), 1, 12000);
        if (diagnostic) Assert.True(ModelRequestAdmission.EstimateTokens(request) > 2000);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["answer"] = "observed" }, request.StructuredOutputSchema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["answer"] = "invented" }, request.StructuredOutputSchema));
        Assert.Equal(0, state.ModelCalls);
        Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldVisibilityRemainsActiveWhenFullGrammarPresentationCannotFit(bool diagnostic)
    {
        var state = Session(diagnostic);
        var schema = Schema();
        var phase = Navigation(state);
        // Large original schema descriptions remain in the authoritative grammar,
        // while the compact guide stays active without evicting original context.
        phase["targetInputTokens"] = 2000; phase["maxInputTokens"] = 4000;
        var compact = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, Prompt, schema);
        Assert.Contains("response: {answer}", compact.Prompt, StringComparison.Ordinal);
        Assert.EndsWith(Prompt[(Prompt.LastIndexOf('\n') + 1)..], compact.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', 9000), compact.Prompt, StringComparison.Ordinal);
        var originalTokens = checked((int)ModelRequestAdmission.EstimateTokens(compact));
        phase["targetInputTokens"] = originalTokens + 1; phase["maxInputTokens"] = originalTokens + 20;
        var retained = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, Prompt, schema);
        Assert.Equal(compact.Prompt, retained.Prompt);
        Assert.Equal(compact.ClientRequestId, retained.ClientRequestId);
        Assert.True(JsonNode.DeepEquals(compact.StructuredOutputSchema, retained.StructuredOutputSchema));
        Assert.InRange(ModelRequestAdmission.EstimateTokens(retained), 1, originalTokens + 1);
        Assert.Null(state.PendingCall);
    }

    [Fact]
    public void StrictFollowupKeepsAllContextWhenOnlyOptionalGuidanceWouldCrossItsCeiling()
    {
        var state = Session(false);
        var phase = Navigation(state);
        var original = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, Prompt, Schema());
        Assert.Contains("Discovery response field guide", original.Prompt, StringComparison.Ordinal);
        var withGuide = ModelRequestAdmission.EstimateTokens(original);
        var withoutGuide = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        withoutGuide.Prompt = Prompt;
        var limit = checked((int)ModelRequestAdmission.EstimateTokens(withoutGuide) + 10);
        Assert.True(withGuide > limit);
        phase["targetInputTokens"] = limit; phase["maxInputTokens"] = limit;
        var request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.DiscoveryPurpose, Prompt, Schema());
        Assert.Equal(Prompt, request.Prompt);
        Assert.InRange(ModelRequestAdmission.EstimateTokens(request), 1, limit);
        Assert.True(JsonNode.DeepEquals(original.StructuredOutputSchema, request.StructuredOutputSchema));
        Assert.Null(state.PendingCall);
    }

    private static PlanningSession Session(bool diagnostic)
    {
        var state = new PlanningSession { Request = new() { TenantId = "presentation", Prompt = "Keep the original contract.",
            MaxModelCalls = 64, MaxReplanAttempts = 4, Generation = new("medium", 12000, 8192) } };
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        return PlannerFixture.Capture(state);
    }

    private static JsonObject Navigation(PlanningSession state) =>
        state.Request.Options["construction_limits"]!["phasePolicy"]!["navigation"]!.AsObject();

    private static JsonObject Schema() => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["answer"] = new JsonObject { ["$ref"] = "#/$defs/answer" } },
        ["required"] = new JsonArray("answer"),
        ["$defs"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "string", ["pattern"] = "^observed$", ["description"] = new string('c', 9000) } }
    };
}
