using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class IndependentProviderProbeTests
{
    [Fact]
    public void ProviderDecoderProbePreservesTheExactSchemaAndFullHostValidation()
    {
        var original = new LLMRequest { Provider = "p", Model = "m", Prompt = "Retain every original obligation.", Reasoning = "medium", MaxTokens = 32768,
            StructuredOutputStrict = true, StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"requiredResult":{"type":"string"}},"required":["requiredResult"],"additionalProperties":false}""") };
        var frozen = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        var control = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "control", "first", "medium", 32768);
        var changed = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "decoder", "second", "medium", 32768, structuredStrict: false);
        Assert.True(control.StructuredOutputStrict); Assert.False(changed.StructuredOutputStrict);
        Assert.NotEqual(control.ClientRequestId, changed.ClientRequestId);
        var normalized = JsonSerializer.SerializeToNode(changed, PlanningJsonContext.Default.LLMRequest)!;
        normalized["clientRequestId"] = control.ClientRequestId; normalized["structuredOutputStrict"] = true;
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(control, PlanningJsonContext.Default.LLMRequest), normalized));
        Assert.Equal(frozen, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
        foreach (var request in new[] { control, changed })
        {
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject(), request.StructuredOutputSchema!));
            Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["requiredResult"] = "observed" }, request.StructuredOutputSchema!));
        }
    }

    [Fact]
    public void ResponsesInspectionPreservesTheExactGatewayDeploymentPrefixAndQuery()
    {
        var route = KeyVaultBenchmarkModel.ResponsesRoute(new("https://gateway.invalid/authorized/deployments/model/chat/completions?api-version=version"));
        Assert.Equal("https://gateway.invalid/authorized/deployments/model/responses?api-version=version", route.ToString());
        Assert.Throws<ArgumentException>(() => KeyVaultBenchmarkModel.ResponsesRoute(new("https://gateway.invalid/unknown")));
        Assert.Throws<ArgumentException>(() => KeyVaultBenchmarkModel.ResponsesRoute(new("https://user@gateway.invalid/chat/completions")));
    }
    [Fact]
    public void RetainedFragmentKeepsExactPromptAndSchemaWithoutContinuingItsIdentity()
    {
        var original = new LLMRequest { Provider = "p", Model = "m", ClientRequestId = "exhausted:17", Reasoning = "medium",
            MaxTokens = 32768, RequiredReviewCalls = 2, Prompt = "Original obligations, contracts and diagnostics",
            StructuredOutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["contract"] = new JsonObject { ["const"] = "unchanged" } } } };
        var retained = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        var probe = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "experiment-1", "nonce-1", "low", 32768);
        var second = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "experiment-2", "nonce-2", "low", 32768);
        Assert.Equal(retained, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(original.Prompt, probe.Prompt);
        Assert.True(JsonNode.DeepEquals(original.StructuredOutputSchema, probe.StructuredOutputSchema));
        Assert.NotEqual(original.ClientRequestId, probe.ClientRequestId); Assert.NotEqual(probe.ClientRequestId, second.ClientRequestId);
        Assert.Equal("low", probe.Reasoning); Assert.Equal(32768, probe.MaxTokens);
        Assert.True(probe.RequireOutputTokenLimit); Assert.True(probe.DisableTransportRetries);
        Assert.True(KeyVaultBenchmarkModel.CreateDispatchRequest(probe, "p", "m", execution: true).DisableTransportRetries);
        Assert.Null(probe.RequiredReviewCalls); Assert.False(probe.UseBackgroundMode); Assert.Null(probe.Tools);
        probe.StructuredOutputSchema!["properties"]!["contract"]!["const"] = "changed probe";
        Assert.Equal(retained, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
    }

    [Fact]
    public void ProbeCannotChangeDeploymentOrBorrowToolAndBackgroundAuthority()
    {
        var original = new LLMRequest { Provider = "p", Model = "m", Prompt = "x", StructuredOutputSchema = new JsonObject() };
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.IndependentProbe(original, "different", "m", "r", "n", "low", 32768));
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.IndependentProbe(original, "p", "different", "r", "n", "low", 32768));
        original.UseBackgroundMode = true;
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "r", "n", "low", 32768));
        original.UseBackgroundMode = false; original.Tools = [new() { Name = "external_effect" }];
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "r", "n", "low", 32768));
    }

    [Fact]
    public void AvailabilityProbeRequiresExactNonceAndExplicitOutputBound()
    {
        var probe = ServerLiveDiagnostics.IndependentProbe(null, "p", "m", "availability", "unique", "low", 32768);
        Assert.Equal("unique", probe.StructuredOutputSchema!["properties"]!["nonce"]!["const"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.IndependentProbe(null, "p", "m", "r", "n", "low", 0));
    }
}
