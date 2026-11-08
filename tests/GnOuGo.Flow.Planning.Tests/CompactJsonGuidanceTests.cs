using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactJsonGuidanceTests
{
    [Theory]
    [InlineData("construction_discovery")]
    [InlineData("construction_draft")]
    public void FreshIssuedRequestsIncludeSyntaxGuidanceBeforeIdentityWithoutChangingChoices(string purpose)
    {
        var state = Session();
        const string prompt = "Preserve original obligations.\n{\"original\":\"Exact text with spaces\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{"answer":{"type":"string","enum":["observed"]}},"required":["answer"],"additionalProperties":false}""")!.AsObject();
        var before = schema.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema);
        Assert.Contains("without formatting whitespace outside strings", request.Prompt, StringComparison.Ordinal);
        Assert.EndsWith(prompt[prompt.LastIndexOf('\n')..], request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, schema.ToJsonString());
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"answer":"observed"}""")!, request.StructuredOutputSchema!));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"answer":"invented"}""")!, request.StructuredOutputSchema!));
        Assert.Equal(request.ClientRequestId, PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema).ClientRequestId);
        Assert.Null(state.PendingCall);
        Assert.Equal(0, state.ModelCalls);
    }

    [Theory]
    [InlineData("construction_draft", "draft")]
    public void StrictAdmissionOmitsOnlyOptionalSyntaxGuidanceAtTheExistingInputCeiling(string purpose, string phase)
    {
        var state = Session();
        const string prompt = "Preserve original obligations.\n{\"original\":\"Exact text with spaces\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""")!.AsObject();
        schema["description"] = new string('c', 9000);
        var original = PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema);
        var bare = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        bare.Prompt = prompt;
        var limit = checked((int)ModelRequestAdmission.EstimateTokens(bare) + 1);
        Assert.True(ModelRequestAdmission.EstimateTokens(original) > limit);
        var captured = state.Request.Options["construction_limits"]!["phasePolicy"]![phase]!;
        captured["targetInputTokens"] = limit; captured["maxInputTokens"] = limit;
        var request = PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema);
        Assert.Equal(prompt, request.Prompt);
        Assert.InRange(ModelRequestAdmission.EstimateTokens(request), 1, limit);
        Assert.True(JsonNode.DeepEquals(original.StructuredOutputSchema, request.StructuredOutputSchema));
        Assert.NotEqual(original.ClientRequestId, request.ClientRequestId);
        Assert.Equal(request.ClientRequestId, PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema).ClientRequestId);
    }

    private static PlanningSession Session() => PlannerFixture.Capture(new PlanningSession
    {
        Request = new() { TenantId = "syntax", Prompt = "Keep the original contract.",
            Generation = new("medium", 12000, 8192) }
    });

    [Theory]
    [InlineData("Choose a permitted action.")]
    [InlineData("Preserve this original:\n{\"businessText\":\"Keep the requested text exactly.\"}\nThen select a permitted action.")]
    public void SyntaxGuidancePreservesTheEntireOriginalPromptAndContext(string instruction)
    {
        var context = new JsonObject { ["original"] = "Exact user text including spaces", ["allowed"] = new JsonArray("first", "second") }.ToJsonString();
        var prompt = instruction + "\n" + context;
        var presented = PlanningPromptPresentation.PresentCompactJsonGuidance(prompt);
        Assert.StartsWith(instruction, presented, StringComparison.Ordinal);
        Assert.EndsWith("\n" + context, presented, StringComparison.Ordinal);
        Assert.Contains("without formatting whitespace outside strings", presented, StringComparison.Ordinal);
        Assert.Equal(JsonNode.Parse(context)!.ToJsonString(), JsonNode.Parse(presented[(presented.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!.ToJsonString());
    }
}
