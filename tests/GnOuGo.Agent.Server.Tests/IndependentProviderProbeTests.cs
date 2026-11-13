using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class IndependentProviderProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedSchemaExperimentRequiresTheExactBaselineAndNeverMutatesSource(bool stale)
    {
        var request = new LLMRequest { Prompt = "Original phase. Old context.", StructuredOutputStrict = true,
            StructuredOutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } };
        var frozen = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["answer"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("answer"), ["additionalProperties"] = false };
        var amendment = new JsonObject { ["expectedPromptHash"] = PlanningGraphCompiler.Fingerprint(request.Prompt),
            ["before"] = request.Prompt, ["after"] = "Original phase. Reduced context.", ["occurrences"] = 1,
            ["expectedSchemaHash"] = stale ? "stale" : PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema.ToJsonString()),
            ["responseSchema"] = schema };
        if (stale)
        {
            Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.AmendProbePrompt(request, "fresh", "nonce", amendment));
            Assert.Equal(frozen, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        }
        else
        {
            var evidence = ServerLiveDiagnostics.AmendProbePrompt(request, "fresh", "nonce", amendment);
            Assert.True(evidence["response_schema_changed"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(schema, request.StructuredOutputSchema));
            request.StructuredOutputSchema!["required"]!.AsArray().Clear();
            Assert.Single(schema["required"]!.AsArray());
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("stale")]
    [InlineData("count")]
    [InlineData("missing")]
    [InlineData("tools")]
    [InlineData("background")]
    [InlineData("schema")]
    [InlineData("empty")]
    [InlineData("same")]
    public void PromptEvidenceExperimentIsAtomicAndPreservesSourceAndTransport(string change)
    {
        var original = new LLMRequest { Provider = "p", Model = "m", Reasoning = "low", MaxTokens = 32768,
            Prompt = "Original requirement. Captured host fact. Original output contract.",
            StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var source = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        var request = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "new", "nonce", null, 32768);
        var amendment = new JsonObject { ["expectedPromptHash"] = PlanningGraphCompiler.Fingerprint(request.Prompt),
            ["before"] = "Captured host fact.", ["after"] = "Corrected captured host fact.", ["occurrences"] = 1 };
        if (change == "stale") amendment["expectedPromptHash"] = new string('0', 64);
        if (change == "count") amendment["occurrences"] = 2;
        if (change == "missing") amendment["before"] = "Missing fact";
        if (change == "tools") request.Tools = [new() { Name = "external_effect" }];
        if (change == "background") request.UseBackgroundMode = true;
        if (change == "schema") request.StructuredOutputSchema = null;
        if (change == "empty") amendment["before"] = "";
        if (change == "same") amendment["after"] = amendment["before"]!.DeepClone();
        var before = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        if (change != "valid")
        {
            Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.AmendProbePrompt(request, "new", "nonce", amendment));
            Assert.Equal(before, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        }
        else
        {
            var evidence = ServerLiveDiagnostics.AmendProbePrompt(request, "new", "nonce", amendment);
            Assert.False(evidence["source_session_modified"]!.GetValue<bool>());
            Assert.Equal("Original requirement. Corrected captured host fact. Original output contract.", request.Prompt);
            var expected = JsonNode.Parse(before)!; expected["prompt"] = request.Prompt; expected["clientRequestId"] = request.ClientRequestId;
            Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)));
        }
        Assert.Equal(source, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
    }

    [Theory]
    [InlineData("low")]
    [InlineData(null)]
    public void SourceReasoningIsPreservedUnlessExplicitlyOverridden(string? reasoning)
    {
        var original = new LLMRequest { Provider = "p", Model = "m", Reasoning = reasoning, MaxTokens = 32768,
            Prompt = "Preserve the exact source parameters.", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var before = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        var preserved = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "copy", "one", null, 32768);
        Assert.Equal(reasoning, preserved.Reasoning);
        var changed = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "override", "two", "medium", 32768);
        Assert.Equal("medium", changed.Reasoning);
        Assert.Equal(before, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
        Assert.NotEqual(preserved.ClientRequestId, changed.ClientRequestId);
    }

    [Fact]
    public void DependencyPresentationChangesOnlyPromptAndIndependentIdentity()
    {
        var original = new LLMRequest { Provider = "p", Model = "m", Prompt = "Original business instructions.\n{\"availableResults\":[]}",
            StructuredOutputStrict = true, StructuredOutputSchema = JsonNode.Parse("""{"title":"construction_select","type":"object","properties":{"action":{"type":"object"}},"required":["action"],"additionalProperties":false}""") };
        var request = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "dependencies", "nonce", "medium", 32768);
        var before = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!;
        ServerLiveDiagnostics.PresentProbeBusinessDependencyFields(request, "dependencies", "nonce");
        Assert.Equal(PlanningPromptPresentation.PresentBusinessDependencyFields(original.Prompt), request.Prompt);
        Assert.NotEqual(before["clientRequestId"]!.ToString(), request.ClientRequestId);
        Assert.EndsWith("\n{\"availableResults\":[]}", request.Prompt, StringComparison.Ordinal);
        var after = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!;
        after["clientRequestId"] = before["clientRequestId"]!.DeepClone(); after["prompt"] = before["prompt"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after));
        request.UseBackgroundMode = true;
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.PresentProbeBusinessDependencyFields(request, "dependencies", "nonce"));
        request.UseBackgroundMode = false; request.StructuredOutputSchema!["title"] = "another_phase";
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.PresentProbeBusinessDependencyFields(request, "dependencies", "nonce"));
    }

    [Fact]
    public void IntentFieldPresentationChangesOnlyPromptAndIndependentIdentity()
    {
        var original = new LLMRequest { Provider = "p", Model = "m", Prompt = "Original intent instructions.\n{\"sources\":[{\"text\":\"Return a numeric result.\"}]}",
            StructuredOutputStrict = true, StructuredOutputSchema = JsonNode.Parse("""{"title":"construction_intent","type":"object","properties":{"value":{"type":"number"}},"required":["value"],"additionalProperties":false}""") };
        var request = ServerLiveDiagnostics.IndependentProbe(original, "p", "m", "fields", "nonce", "medium", 32768);
        var before = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!;
        ServerLiveDiagnostics.PresentProbeIntentDeclarationFields(request, "fields", "nonce");
        Assert.Equal(PlanningPromptPresentation.PresentIntentDeclarationFields(original.Prompt), request.Prompt);
        Assert.NotEqual(before["clientRequestId"]!.ToString(), request.ClientRequestId);
        Assert.EndsWith("\n{\"sources\":[{\"text\":\"Return a numeric result.\"}]}", request.Prompt, StringComparison.Ordinal);
        var after = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!;
        after["clientRequestId"] = before["clientRequestId"]!.DeepClone(); after["prompt"] = before["prompt"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after));
        request.UseBackgroundMode = true;
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.PresentProbeIntentDeclarationFields(request, "fields", "nonce"));
        request.UseBackgroundMode = false; request.StructuredOutputSchema!["title"] = "another_phase";
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.PresentProbeIntentDeclarationFields(request, "fields", "nonce"));
    }

    [Fact]
    public void VisibleResponseContractChangesOnlyTheIndependentPromptAndIdentity()
    {
        var request = ServerLiveDiagnostics.IndependentProbe(null, "p", "m", "visibility", "nonce", "low", 32768);
        request.StructuredOutputStrict = true;
        request.Prompt += "\n{\"baseHash\":\"retained-context\"}";
        var before = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!;
        var originalPrompt = request.Prompt;
        ServerLiveDiagnostics.PresentProbeResponseSchema(request, "visibility", "nonce");
        Assert.NotEqual(before["clientRequestId"]!.ToString(), request.ClientRequestId);
        Assert.StartsWith(originalPrompt[..originalPrompt.LastIndexOf('\n')], request.Prompt, StringComparison.Ordinal);
        Assert.Contains(request.StructuredOutputSchema!.ToJsonString(), request.Prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n{\"baseHash\":\"retained-context\"}", request.Prompt, StringComparison.Ordinal);
        var after = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!;
        after["clientRequestId"] = before["clientRequestId"]!.DeepClone(); after["prompt"] = before["prompt"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.True(request.StructuredOutputStrict);
        request.UseBackgroundMode = true;
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.PresentProbeResponseSchema(request, "visibility", "nonce"));
    }

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
