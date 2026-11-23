using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkFinalizedSdkRetentionTests
{
    [Fact]
    public void ColdReaderVerifiesTheIssuedHashIncludingBothUtcTimestamps()
    {
        var start = DateTimeOffset.UnixEpoch.AddMicroseconds(372757);
        var original = new JsonObject { ["reservation_recorded_utc"] = start, ["failure_recorded_utc"] = start.AddMilliseconds(24), ["receipt"] = "verified" };
        var issued = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var cold = JsonNode.Parse(original.ToJsonString())!.AsObject();
        Assert.Equal(issued, BenchmarkCampaign.FinalizedSdkEvidenceHash(original));
        Assert.Equal(issued, BenchmarkCampaign.FinalizedSdkEvidenceHash(cold));
        cold["receipt"] = "changed";
        Assert.NotEqual(issued, BenchmarkCampaign.FinalizedSdkEvidenceHash(cold));
        cold["receipt"] = "verified";
        cold["failure_recorded_utc"] = start;
        Assert.NotEqual(issued, BenchmarkCampaign.FinalizedSdkEvidenceHash(cold));
        cold["reservation_recorded_utc"] = "1970-01-01T00:00:00.372757Z";
        Assert.Throws<InvalidOperationException>(() => BenchmarkCampaign.FinalizedSdkEvidenceHash(cold));
    }

    [Fact]
    public void ExecutionUsesTheSubmittedFieldsWithinTheServerChatEnvelope()
    {
        var submitted = new JsonObject { ["query"] = "live", ["optional"] = null };
        var envelope = submitted.DeepClone().AsObject();
        envelope["conversation_id"] = "original";
        Assert.True(ServerBusinessExecution.MatchesSubmittedInputs(submitted, envelope));
        envelope.Remove("optional");
        Assert.False(ServerBusinessExecution.MatchesSubmittedInputs(submitted, envelope));
        envelope["optional"] = null;
        envelope["query"] = "changed";
        Assert.False(ServerBusinessExecution.MatchesSubmittedInputs(submitted, envelope));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("active")]
    [InlineData("cleanup")]
    [InlineData("unobserved")]
    [InlineData("pending")]
    [InlineData("other failure")]
    [InlineData("tool effect")]
    [InlineData("modified file")]
    [InlineData("earlier request")]
    [InlineData("late failure")]
    [InlineData("missing receipt")]
    [InlineData("different receipt")]
    [InlineData("duplicate event")]
    public void ExecutionInspectionBindsEveryExternalReceiptAndTheSdkFailureInterval(string? defect)
    {
        var from = DateTimeOffset.UnixEpoch;
        var until = from.AddSeconds(5);
        var result = new JsonObject { ["success"] = false, ["error_code"] = "COPILOT_TURN_FAILED",
            ["toolExecutions"] = new JsonArray(), ["modifiedFiles"] = new JsonArray() };
        if (defect == "tool effect") result["toolExecutions"]!.AsArray().Add("effect");
        if (defect == "modified file") result["modifiedFiles"]!.AsArray().Add("file");
        var arguments = new JsonObject { ["task"] = "review", ["projectRoot"] = "owned" };
        var invocation = new WorkflowInvocation { Id = "sdk", StepType = "mcp.call", Status = "failed", Recovery = StepRecovery.External,
            Error = new() { Code = "MCP_CALL_ERROR" }, ExternalCompletionObserved = defect != "unobserved",
            ResolvedInput = new JsonObject { ["server"] = BenchmarkCampaign.SdkServer, ["method"] = "code_agent_edit", ["request"] = arguments.DeepClone() },
            DispatchedAt = from, CompletedAt = until, Observation = new JsonObject { ["status"] = "error", ["response"] = result.DeepClone() } };
        var run = new WorkflowRun { Status = defect == "active" ? "running" : "failed", FinalizationStarted = true,
            FinalizationCompleted = defect != "cleanup", Result = new() { Success = false } };
        run.Invocations["sdk"] = invocation;
        if (defect is "pending" or "other failure")
            run.Invocations["other"] = new() { Id = "other", StepType = "mcp.call", Status = defect == "pending" ? "needs_reconciliation" : "failed" };
        var events = new JsonArray(new JsonObject { ["server"] = BenchmarkCampaign.SdkServer, ["tool"] = "code_agent_edit",
            ["arguments"] = arguments, ["result"] = result, ["error"] = true, ["started"] = from.AddSeconds(1) });
        if (defect == "different receipt") result["error_code"] = "another error";
        if (defect == "missing receipt") events.Clear();
        if (defect == "duplicate event") events.Add(events[0]!.DeepClone());
        JsonArray Inspect() => BenchmarkCampaign.FinalizedSdkEffects(run, events,
            defect == "earlier request" ? from.AddSeconds(-1) : from.AddSeconds(2), defect == "late failure" ? until.AddSeconds(1) : until);
        if (defect is null)
        {
            var proof = Assert.Single(Inspect())!;
            Assert.Equal("failed", proof["status"]!.GetValue<string>());
            Assert.DoesNotContain("projectRoot", proof.ToJsonString());
        }
        else Assert.Throws<InvalidOperationException>(Inspect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tools")]
    [InlineData("background")]
    [InlineData("continuation")]
    [InlineData("receipt")]
    [InlineData("partial")]
    [InlineData("usage")]
    [InlineData("hold")]
    [InlineData("identity")]
    [InlineData("policy")]
    [InlineData("endpoint")]
    [InlineData("wire digest")]
    [InlineData("endpoint digest")]
    [InlineData("versioned")]
    [InlineData("wrong version")]
    public void FinancialInspectionRequiresAnUnreceivedTextDecisionAndRetainsItsFullHold(string? defect)
    {
        var body = new JsonObject { ["model"] = "deployment", ["max_completion_tokens"] = 256,
            ["response_format"] = new JsonObject { ["type"] = "json_schema" }, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "test" }) };
        if (defect == "tools") body["tools"] = new JsonArray("hosted");
        if (defect == "background") body["background"] = true;
        if (defect == "continuation") body["previous_response_id"] = "old";
        const string endpoint = "https://provider.example/v1/chat/completions", id = "run-copilot:call";
        var text = body.ToJsonString();
        var request = JsonSerializer.SerializeToNode(new LLMRequest { ClientRequestId = id, Provider = "provider", Model = "deployment", Prompt = text, MaxTokens = 256 },
            PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var attempt = new JsonObject { ["Id"] = defect == "identity" ? "other" : id };
        if (defect == "receipt") attempt["ProviderResponseId"] = "received";
        if (defect == "partial") attempt["PartialBody"] = "fragment";
        var originalUri = defect is "versioned" or "wrong version" ? endpoint + "?api-version=stable-1" : endpoint;
        var fp = PlanningGraphCompiler.Fingerprint(defect == "wire digest" ? "changed" : "POST\n" + originalUri + "\n" + text);
        var policy = new JsonObject { ["version"] = 2, ["mode"] = "sdk_proxy_single_attempt", ["sdk_inference_transport"] = BenchmarkCampaign.SdkStructuredTransport,
            ["disable_transport_retries"] = true, ["allow_http_recovery"] = true,
            ["effective_retry_policy"] = new JsonObject { ["MaxAttempts"] = 1, ["MaxUncertainRetries"] = 0 }, ["request_hash"] = PlanningGraphCompiler.Fingerprint(request.ToJsonString()),
            ["endpoint_sha256"] = PlanningGraphCompiler.Fingerprint(defect == "endpoint digest" ? "changed" : originalUri), ["wire_fingerprint"] = fp };
        var http = new JsonObject { ["input_ceiling"] = 100L, ["output_ceiling"] = 256L, ["cost_ceiling_eur"] = defect == "hold" ? 0m : 3.29m,
            ["exchange_quote"] = new JsonObject(), ["effective_transport_policy"] = policy, ["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(policy.ToJsonString()),
            ["transport"] = new JsonObject { ["Fingerprint"] = fp, ["Endpoint"] = endpoint, ["RequestMethod"] = "POST", ["RequestBody"] = text, ["Attempts"] = new JsonArray(attempt) } };
        if (defect == "usage") http["usage"] = new JsonObject();
        if (defect == "policy") policy["mode"] = "changed";
        var frozen = new JsonObject { ["provider"] = "provider", ["model"] = "deployment", ["endpoint"] = defect == "endpoint" ? "https://elsewhere.example" : "https://provider.example/v1" };
        JsonObject Inspect() => BenchmarkCampaign.VerifyUnreceivedSdkWire(id, request, http, frozen,
            defect switch { "versioned" => "stable-1", "wrong version" => "stable-2", _ => null });
        if (defect is null or "versioned") Assert.Equal(3.29m, Inspect()["reserved_cost_eur"]!.GetValue<decimal>());
        else Assert.Throws<InvalidOperationException>(Inspect);
    }
}
