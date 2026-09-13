using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SingletonOutputDiagnosticTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMRequest Source() => new()
    {
        ClientRequestId = "archived-request", Provider = "configured", Model = "configured-model", Prompt = "EXACT_PRIVATE_PROMPT",
        Temperature = 0.2, Reasoning = "low", MaxTokens = 8192, RequireOutputTokenLimit = true, DisableTransportRetries = true, StructuredOutputStrict = true,
        StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"decision":{"type":"string","enum":["same_as:issued","not_a_declaration"]}},"required":["decision"],"additionalProperties":false}""")
    };

    [Fact]
    public void CloneChangesOnlyOutputAndIdentityAndSurvivesTheJournalPolicy()
    {
        var source = Source(); var before = JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest);
        var request = SingletonOutputDiagnostic.CreateRequest(source);
        PlanningGenerationPolicy.Apply(request, new() { MaxOutputTokens = 16384 });
        SingletonOutputDiagnostic.RequireOnlyOutputChange(source, request);
        Assert.Equal(16384, request.MaxTokens); Assert.Equal("low", request.Reasoning);
        Assert.StartsWith(SingletonOutputDiagnostic.Identity + ":", request.ClientRequestId);
        Assert.Equal(before, JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest));
        Assert.NotSame(source.StructuredOutputSchema, request.StructuredOutputSchema);
        Assert.Equal(request.ClientRequestId, SingletonOutputDiagnostic.CreateRequest(source).ClientRequestId);
        request.Prompt += "changed";
        Assert.Throws<InvalidOperationException>(() => SingletonOutputDiagnostic.RequireOnlyOutputChange(source, request));
        Assert.Throws<InvalidOperationException>(() => SingletonOutputDiagnostic.RequireOnlyOutputChange(source, PlanningGenerationPolicy.Apply(SingletonOutputDiagnostic.CreateRequest(source), new())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneReservedDispatchReplaysWithoutChangingArchiveOrResettingBudget(bool truncated)
    {
        await using var archive = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        await using var isolated = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var source = Source();
        var seed = new LLMUsageBudgetSnapshot { StartedAtUtc = DateTimeOffset.UtcNow, Calls = 6, InputTokens = 200, OutputTokens = 100, TotalTokens = 300 };
        var seedJson = JsonSerializer.Serialize(seed, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var sourceState = new PlanningSnapshot { Request = new() { TenantId = SingletonOutputDiagnostic.Tenant, SessionId = "archive", Prompt = "PRIVATE_ARCHIVE" }, Status = PlanningStatus.Stopped };
        Assert.True(await archive.Store.TrySaveAsync(sourceState, null, Ct));
        await archive.Records.UpsertAsync(PlanningBudgetSink.Collection, SingletonOutputDiagnostic.Tenant, "archive", seedJson, EfPlanningSessionStore.Author, Ct);
        var before = JsonSerializer.Serialize(await archive.Store.LoadAsync(SingletonOutputDiagnostic.Tenant, "archive", Ct), PlanningJsonContext.Default.PlanningSnapshot);
        var client = new Client(async request =>
        {
            SingletonOutputDiagnostic.RequireOnlyOutputChange(source, request);
            await using var db = isolated.CreateDbContext();
            Assert.Equal("reserved", (await db.Calls.SingleAsync(Ct)).Status);
            Assert.Empty(await db.Sessions.ToListAsync(Ct));
            var budgetRecord = await archive.Records.GetAsync(PlanningBudgetSink.Collection, SingletonOutputDiagnostic.Tenant, SingletonOutputDiagnostic.Identity, EfPlanningSessionStore.Author, Ct);
            Assert.Equal(7, JsonSerializer.Deserialize(budgetRecord!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!.Calls);
            return new LLMResponse { CompletionStatus = truncated ? "output_limit" : null, Json = truncated ? null : new JsonObject { ["decision"] = "not_a_declaration" },
                Usage = JsonNode.Parse("""{"prompt_tokens":20,"completion_tokens":30,"completion_tokens_details":{"reasoning_tokens":10}}""") };
        });
        var first = await SingletonOutputDiagnostic.ExecuteOnceAsync(source, seed, new() { MaxCalls = 100 }, client, new Estimator(), null, isolated, archive.Records, Ct);
        var replay = await SingletonOutputDiagnostic.ExecuteOnceAsync(source, seed, new() { MaxCalls = 100 }, client, new Estimator(), null, isolated, archive.Records, Ct);
        Assert.Equal(1, client.Calls); Assert.Equal(first.CompletionStatus, replay.CompletionStatus);
        Assert.True(JsonNode.DeepEquals(first.Json, replay.Json));
        var saved = await archive.Records.GetAsync(PlanningBudgetSink.Collection, SingletonOutputDiagnostic.Tenant, SingletonOutputDiagnostic.Identity, EfPlanningSessionStore.Author, Ct);
        Assert.Equal(7, JsonSerializer.Deserialize(saved!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!.Calls);
        Assert.Equal(seedJson, (await archive.Records.GetAsync(PlanningBudgetSink.Collection, SingletonOutputDiagnostic.Tenant, "archive", EfPlanningSessionStore.Author, Ct))!.Value);
        Assert.Equal(before, JsonSerializer.Serialize(await archive.Store.LoadAsync(SingletonOutputDiagnostic.Tenant, "archive", Ct), PlanningJsonContext.Default.PlanningSnapshot));
        var changed = Source(); changed.Prompt += "altered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => SingletonOutputDiagnostic.ExecuteOnceAsync(changed, seed, new() { MaxCalls = 100 }, client, new Estimator(), null, isolated, archive.Records, Ct));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task MissingReceiptCannotDispatchAgain()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new Client(_ => throw new IOException("Simulated provider disconnect"));
        var source = Source(); var seed = new LLMUsageBudgetSnapshot { StartedAtUtc = DateTimeOffset.UtcNow, Calls = 6 };
        await Assert.ThrowsAsync<IOException>(() => SingletonOutputDiagnostic.ExecuteOnceAsync(source, seed, new() { MaxCalls = 100 }, client, new Estimator(), null, fixture, fixture.Records, Ct));
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => SingletonOutputDiagnostic.ExecuteOnceAsync(source, seed, new() { MaxCalls = 100 }, client, new Estimator(), null, fixture, fixture.Records, Ct));
        Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetUnverifiable, error.Code); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void AssessmentPreservesExactAssignmentAndDistinguishesUnknownUsage()
    {
        var response = new LLMResponse { Json = new JsonObject { ["decision"] = "not_a_declaration" },
            Usage = JsonNode.Parse("""{"completion_tokens":100,"completion_tokens_details":{"reasoning_tokens":60}}""") };
        var result = SingletonOutputDiagnostic.Assess(response, Source().StructuredOutputSchema!);
        Assert.Equal("completed", result["completion"]!.ToString()); Assert.True(result["schemaValid"]!.GetValue<bool>());
        Assert.Equal(40, result["finalAnswerTokens"]!.GetValue<long>()); Assert.True(JsonNode.DeepEquals(response.Json, result["assignment"]));
        response.Json["decision"] = "foreign"; response.Usage = null;
        result = SingletonOutputDiagnostic.Assess(response, Source().StructuredOutputSchema!);
        Assert.False(result["schemaValid"]!.GetValue<bool>()); Assert.Null(result["reasoningTokens"]); Assert.Null(result["finalAnswerTokens"]);
        response.Json = null; response.CompletionStatus = "output_limit";
        result = SingletonOutputDiagnostic.Assess(response, Source().StructuredOutputSchema!);
        Assert.Equal("output_limit", result["completion"]!.ToString()); Assert.Null(result["schemaValid"]); Assert.Null(result["assignment"]);
    }

    private sealed class Client(Func<LLMRequest, Task<LLMResponse>> call) : ILLMClient
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) { Calls++; return call(request); }
    }
    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0;
    }
}
