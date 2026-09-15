using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningOutputEscalationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("completed")]
    [InlineData("unverifiable")]
    [InlineData("budget")]
    [InlineData("cancelled")]
    [InlineData("forged")]
    [InlineData("missing")]
    public async Task JournalRequiresDurableExactParentAndNeverRedispatchesEscalation(string outcome)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new Client();
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = outcome == "budget" ? 1 : 3 });
        PlanningModelJournal Journal() => new(client, fixture, fixture.Records, "tenant", "session", budget, new Estimator());
        var parent = new LLMRequest { Model = "fake", Prompt = "owned evidence", Reasoning = "low", StructuredOutputStrict = true,
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"choice":{"type":"boolean"}},"required":["choice"],"additionalProperties":false}""") };
        PlanningPersistenceTests.Identify(parent, "session");
        parent.ClientRequestId = parent.ClientRequestId!.Replace(":construction:main:", ":construction:main:page:", StringComparison.Ordinal);
        var receipt = outcome == "missing" ? new LLMResponse { CompletionStatus = "output_limit" } : await Journal().CallAsync(parent, Ct);
        var child = JsonSerializer.Deserialize(JsonSerializer.Serialize(parent, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        child.MaxTokens = 16384; child.OutputBudgetEscalation = new("page", parent.ClientRequestId!, PlanningGenerationPolicy.RequestFingerprint(parent),
            PlanningGenerationPolicy.ReceiptFingerprint(receipt), "choice", "semantic", "evidence");
        if (outcome == "forged") child.Prompt += "changed";
        PlanningPersistenceTests.Identify(child, "session", 2);
        if (outcome is "missing" or "forged")
        {
            await Assert.ThrowsAsync<PlanningConflictException>(() => Journal().CallAsync(child, Ct));
            Assert.Equal(outcome == "missing" ? 0 : 1, client.OutputLimits.Count); return;
        }
        if (outcome is "unverifiable" or "cancelled") client.Failure = outcome;
        if (outcome == "completed") Assert.Equal("completed", (await Journal().CallAsync(child, Ct)).CompletionStatus);
        else if (outcome == "unverifiable") await Assert.ThrowsAsync<IOException>(() => Journal().CallAsync(child, Ct));
        else if (outcome == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Journal().CallAsync(child, Ct));
        else Assert.Equal(ErrorCodes.LlmBudgetExceeded, (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Journal().CallAsync(child, Ct))).Code);
        var calls = budget.Snapshot.Calls;
        if (outcome == "completed") Assert.Equal("completed", (await Journal().CallAsync(child, Ct)).CompletionStatus);
        else Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Journal().CallAsync(child, Ct))).Code);
        Assert.Equal(calls, budget.Snapshot.Calls);
        Assert.Equal(outcome == "budget" ? new[] { 8192 } : new[] { 8192, 16384 }, client.OutputLimits);
    }

    private sealed class Client : ILLMClient
    {
        public string? Failure { get; set; }
        public List<int> OutputLimits { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            OutputLimits.Add(request.MaxTokens!.Value);
            if (Failure == "unverifiable") throw new IOException("lost receipt");
            if (Failure == "cancelled") throw new OperationCanceledException(ct);
            return Task.FromResult(new LLMResponse { CompletionStatus = request.MaxTokens == 8192 ? "output_limit" : "completed",
                Json = new JsonObject { ["choice"] = true }, Usage = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = request.MaxTokens == 8192 ? 8192 : 20 } });
        }
    }
    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0;
    }
}
