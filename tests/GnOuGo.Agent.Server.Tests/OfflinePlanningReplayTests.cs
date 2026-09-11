using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class OfflinePlanningReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayPreservesInvalidAndTruncatedEvidenceWithoutMutatingIt(bool truncated)
    {
        var request = Request();
        var captured = new LLMResponse { CompletionStatus = truncated ? "output_limit" : "completed",
            Json = truncated ? null : new JsonObject { ["invalid"] = "retained" },
            Usage = new JsonObject { ["output_tokens"] = truncated ? 8192 : 17 } };
        var client = new ReceiptOnlyClient("session", new Dictionary<string, (LLMRequest, LLMResponse?)> { [request.ClientRequestId!] = (request, captured) });
        var first = await client.CallAsync(request, Ct);
        first.Json = new JsonObject(); first.CompletionStatus = "changed";
        var second = await client.CallAsync(request, Ct);
        Assert.Equal(captured.CompletionStatus, second.CompletionStatus);
        Assert.True(JsonNode.DeepEquals(captured.Json, second.Json));
        Assert.True(JsonNode.DeepEquals(captured.Usage, second.Usage));
        Assert.Single(client.Replayed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("owner")]
    [InlineData("identity")]
    [InlineData("request")]
    [InlineData("receipt")]
    public async Task MissingChangedOrUnverifiableRequestsCannotFallBackToDispatch(string defect)
    {
        var request = Request();
        var evidence = new Dictionary<string, (LLMRequest, LLMResponse?)> { [request.ClientRequestId!] = (request, defect == "receipt" ? null : new()) };
        var replay = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        if (defect == "missing") evidence.Clear();
        if (defect == "identity") replay.ClientRequestId = "session:another-reservation:" + request.ClientRequestId!.Split(':')[^1];
        if (defect == "request") replay.Prompt = "Changed private prompt";
        var client = new ReceiptOnlyClient(defect == "owner" ? "other-session" : "session", evidence);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => client.CallAsync(replay, Ct));
        Assert.Equal(defect == "request" ? "REPLAY_REQUEST_CHANGED" : defect == "receipt" ? ErrorCodes.LlmBudgetUnverifiable : "REPLAY_EVIDENCE_REQUIRED", error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.Empty(client.Replayed);
    }

    [Fact]
    public async Task CancellationDoesNotConsumeReplayEvidence()
    {
        var request = Request();
        var client = new ReceiptOnlyClient("session", new Dictionary<string, (LLMRequest, LLMResponse?)> { [request.ClientRequestId!] = (request, new()) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallAsync(request, new CancellationToken(true)));
        Assert.Empty(client.Replayed);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMRequest Request()
    {
        var request = new LLMRequest { Model = "fixture", Prompt = "Captured private prompt", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        request.ClientRequestId = "session:4:1:assessment:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        return request;
    }
}
