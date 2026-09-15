using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CapturedInterpretationDiagnosticTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMRequest Source() => new()
    {
        ClientRequestId = "archive:escalation", Provider = "configured", Model = "model", Prompt = "EXACT_CAPTURED_PROMPT",
        Temperature = 0.2, Reasoning = "low", MaxTokens = 16384, RequireOutputTokenLimit = true, DisableTransportRetries = true,
        UseBackgroundMode = true, StructuredOutputStrict = true,
        OutputBudgetEscalation = new("page", "archive:parent", "request-hash", "receipt-hash", "decision", "canonical", "evidence"),
        StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"decision":{"type":"string","enum":["workflow_policy"]}},"required":["decision"],"additionalProperties":false}""")
    };

    [Fact]
    public void ExactGenerationSurvivesIsolatedPolicyWithoutReusingArchiveAuthority()
    {
        var source = Source(); var before = JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest);
        var copy = CapturedInterpretationDiagnostic.CreateRequest(source);
        CapturedInterpretationDiagnostic.RequireIdenticalGeneration(source, PlanningGenerationPolicy.Apply(copy, new() { MaxOutputTokens = 16384 }));
        Assert.Null(copy.OutputBudgetEscalation); Assert.Equal(16384, copy.MaxTokens);
        Assert.StartsWith(CapturedInterpretationDiagnostic.Identity + ":", copy.ClientRequestId);
        Assert.Equal(copy.ClientRequestId, CapturedInterpretationDiagnostic.CreateRequest(source).ClientRequestId);
        Assert.Equal(before, JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest));
        Assert.NotSame(source.StructuredOutputSchema, copy.StructuredOutputSchema);
        copy.Reasoning = "medium";
        Assert.Throws<InvalidOperationException>(() => CapturedInterpretationDiagnostic.RequireIdenticalGeneration(source, copy));
        Assert.Throws<InvalidOperationException>(() => CapturedInterpretationDiagnostic.RequireIdenticalGeneration(source,
            PlanningGenerationPolicy.Apply(CapturedInterpretationDiagnostic.CreateRequest(source), new())));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("schema")]
    [InlineData("model")]
    [InlineData("reasoning")]
    [InlineData("temperature")]
    [InlineData("transportRetries")]
    [InlineData("outputLimit")]
    public void ChangedGenerationCannotPassExactRequestVerification(string field)
    {
        var source = Source();
        var copy = CapturedInterpretationDiagnostic.CreateRequest(source);
        switch (field)
        {
            case "prompt": copy.Prompt += "changed"; break;
            case "schema": copy.StructuredOutputSchema!["additionalProperties"] = true; break;
            case "model": copy.Model = "other"; break;
            case "reasoning": copy.Reasoning = "medium"; break;
            case "temperature": copy.Temperature = 0.3; break;
            case "transportRetries": copy.DisableTransportRetries = false; break;
            case "outputLimit": copy.MaxTokens = 32768; break;
        }
        Assert.Throws<InvalidOperationException>(() => CapturedInterpretationDiagnostic.RequireIdenticalGeneration(source, copy));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("output_limit")]
    [InlineData("unverifiable")]
    public async Task OneDispatchIsReservedAndNeverRepeatedOrChargedToArchive(string completion)
    {
        await using var archive = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        await using var isolated = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var source = Source(); var seed = new LLMUsageBudgetSnapshot { StartedAtUtc = DateTimeOffset.UtcNow, Calls = 12, InputTokens = 30, OutputTokens = 40, TotalTokens = 70 };
        var archivedBudget = JsonSerializer.Serialize(seed, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        await archive.Records.UpsertAsync(PlanningBudgetSink.Collection, CapturedInterpretationDiagnostic.Tenant, "archive", archivedBudget, EfPlanningSessionStore.Author, Ct);
        var client = new Client(async request =>
        {
            CapturedInterpretationDiagnostic.RequireIdenticalGeneration(source, request);
            await using var db = isolated.CreateDbContext();
            Assert.Empty(await db.Sessions.ToListAsync(Ct));
            Assert.Equal("reserved", (await db.Calls.SingleAsync(Ct)).Status);
            var budget = await archive.Records.GetAsync(PlanningBudgetSink.Collection, CapturedInterpretationDiagnostic.Tenant, CapturedInterpretationDiagnostic.Identity, EfPlanningSessionStore.Author, Ct);
            Assert.Equal(13, JsonSerializer.Deserialize(budget!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!.Calls);
            if (completion == "unverifiable") throw new LLMClientException(LLMClientFailureKind.ServiceUnavailable, "Unavailable", true);
            return new LLMResponse { CompletionStatus = completion, Json = completion == "completed" ? new JsonObject { ["decision"] = "workflow_policy" } : null };
        });
        Task<LLMResponse> Execute(LLMRequest request) => CapturedInterpretationDiagnostic.ExecuteOnceAsync(request, seed,
            new() { MaxCalls = 16 }, client, new Estimator(), null, isolated, archive.Records, Ct);
        if (completion == "unverifiable")
        {
            await Assert.ThrowsAsync<LLMClientException>(() => Execute(source));
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Execute(source));
            Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetUnverifiable, error.Code);
        }
        else
        {
            var first = await Execute(source); var replay = await Execute(source);
            Assert.Equal(first.CompletionStatus, replay.CompletionStatus); Assert.True(JsonNode.DeepEquals(first.Json, replay.Json));
        }
        Assert.Equal(1, client.Calls);
        Assert.Equal(archivedBudget, (await archive.Records.GetAsync(PlanningBudgetSink.Collection, CapturedInterpretationDiagnostic.Tenant, "archive", EfPlanningSessionStore.Author, Ct))!.Value);
        var changed = Source(); changed.Prompt += "different";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(changed));
        Assert.Equal(1, client.Calls);
    }

    private sealed class Client(Func<LLMRequest, Task<LLMResponse>> invoke) : ILLMClient
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) { Calls++; return invoke(request); }
    }
    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0;
    }
}
