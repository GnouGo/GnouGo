using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningModelRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LLMUsageBudgetLimits Limits = new() { MaxCalls = 8, MaxTotalTokens = 1_000_000, MaxEstimatedCost = new(50, "EUR") };

    [Fact]
    public async Task ExplicitRetryRetainsOldDispatchAndChargesNewIdentity()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture); var old = state.PendingCall!;
        await PrepareAsync(state, fixture);
        Assert.Equal(5, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.NotEqual(old.Id, state.PendingCall!.Id);
        Assert.Equal(old.Request.Prompt, state.PendingCall.Request.Prompt); Assert.Equal(100, state.ActiveMilliseconds);
        Assert.True(state.Usage!.InputTokens >= 12_010); Assert.Equal(69, state.Usage.OutputTokens);
        Assert.True(state.Usage.EstimatedCost > 0.01m);
        var client = new Client(); var budget = new LLMUsageBudgetScope(Limits, state.Usage);
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, state.Request.TenantId, state.Request.SessionId, budget, new Estimator());
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => journal.CallAsync(old.Request, Ct));
        Assert.Equal(0, client.Calls);
        await journal.CallAsync(state.PendingCall.Request, Ct);
        Assert.Equal(1, client.Calls); Assert.Equal(5, budget.Snapshot.Calls);
    }

    [Fact]
    public async Task RestartBetweenUsageCorrectionAndSessionCheckpointDoesNotChargeTwice()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        await PrepareAsync(state, fixture); var corrected = state.Usage!; var next = state.PendingCall!.Id;
        var restarted = (await fixture.Store.LoadAsync("planning-tests", "recovery", Ct))!;
        await PrepareAsync(restarted, fixture);
        Assert.Equal(corrected.TotalTokens, restarted.Usage!.TotalTokens);
        Assert.Equal(corrected.EstimatedCost, restarted.Usage.EstimatedCost);
        Assert.Equal(next, restarted.PendingCall!.Id);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("repairs")]
    [InlineData("tokens")]
    [InlineData("cost")]
    public async Task RecoveryCannotReplenishAnExhaustedBudget(string exhausted)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture); var pending = state.PendingCall!.Id;
        if (exhausted == "calls") state.Request.MaxModelCalls = 4;
        if (exhausted == "repairs") { state.PendingCall.Purpose = "replan"; state.ReplanAttempts = 2; }
        var limits = exhausted == "tokens" ? Limits with { MaxTotalTokens = 20 }
            : exhausted == "cost" ? Limits with { MaxEstimatedCost = new(0.02m, "EUR") } : Limits;
        await PlanningModelRecovery.PrepareAsync(state, fixture.Records, limits, new Estimator(), new TestExchangeRateProvider(), Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(4, state.ModelCalls);
        Assert.Equal(pending, state.PendingCall.Id); Assert.Contains(state.Diagnostics, d => d.Code == ErrorCodes.LlmBudgetExceeded);
        Assert.True(state.Usage!.TotalTokens > 15);
    }

    [Fact]
    public async Task LateReceiptReplaysWithoutNewReservationOrEstimatedCharge()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture); var pending = state.PendingCall!;
        await fixture.Records.UpsertAsync(PlanningModelJournal.Collection, state.Request.TenantId,
            state.Request.SessionId + ":" + pending.Id, JsonSerializer.Serialize(new LLMResponse { Text = "completed" }, PlanningJsonContext.Default.LLMResponse), EfPlanningSessionStore.Author, Ct);
        await PrepareAsync(state, fixture);
        Assert.Equal(pending.Id, state.PendingCall!.Id); Assert.Equal(4, state.ModelCalls);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        var budget = await fixture.Records.GetAsync(PlanningBudgetSink.Collection, state.Request.TenantId, state.Request.SessionId, EfPlanningSessionStore.Author, Ct);
        Assert.Equal(15, JsonSerializer.Deserialize(budget!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!.TotalTokens);
    }

    [Fact]
    public async Task HostRetryRequiresCurrentRevisionAndTenantOwnedSession()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog());
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync("recovery", new() { Kind = "retry_model", ExpectedRevision = 1 }, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SubmitAsync("another-tenant-session", new() { Kind = "retry_model" }, Ct));
        var resumed = await service.SubmitAsync("recovery", new() { Kind = "retry_model", ExpectedRevision = 0 }, Ct);
        Assert.Equal(1, resumed.Revision); Assert.Equal(5, resumed.ModelCalls); Assert.Equal(PlanningStatus.Generating, resumed.Status);
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync("recovery", new() { Kind = "retry_model", ExpectedRevision = 0 }, Ct));
    }

    [Fact]
    public async Task RejectedRequestCannotReserveOrRetryAfterRecovery()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        state.Diagnostics = [new("MODEL_REQUEST_REJECTED", "/", "InvalidRequest (HTTP 400)")];
        var revision = state.Revision++;
        Assert.True(await fixture.Store.TrySaveAsync(state, revision, Ct));
        state = (await fixture.Store.LoadAsync("planning-tests", "recovery", Ct))!;
        var baseline = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var budget = await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "planning-tests", "recovery", EfPlanningSessionStore.Author, Ct);
        var ex = await Assert.ThrowsAsync<PlanningConflictException>(() => PrepareAsync(state, fixture));
        Assert.Contains("rejected", ex.Message);
        Assert.Equal(baseline, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(budget, await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "planning-tests", "recovery", EfPlanningSessionStore.Author, Ct));
        Assert.Single(await fixture.Records.ListAsync(PlanningBudgetSink.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct));
    }

    private static Task PrepareAsync(PlanningSession state, PlanningPersistenceTests.StoreFixture fixture)
        => PlanningModelRecovery.PrepareAsync(state, fixture.Records, Limits, new Estimator(), new TestExchangeRateProvider(), Ct);

    private static async Task<PlanningSession> SeedAsync(PlanningPersistenceTests.StoreFixture fixture)
    {
        var request = PlanningGenerationPolicy.Apply(new LLMRequest { Provider = "openai", Model = "gpt-4o-mini", Prompt = "PRIVATE_RETRY_PROMPT", MaxTokens = 64 }, new());
        request.ClientRequestId = "recovery:4:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var state = new PlanningSession { Request = new() { TenantId = "planning-tests", SessionId = "recovery", Prompt = request.Prompt },
            Status = PlanningStatus.Stopped, ModelCalls = 4, ActiveMilliseconds = 100,
            PendingCall = new() { Id = request.ClientRequestId, Purpose = "intent", Request = request },
            Diagnostics = [new("MODEL_DISPATCH_UNVERIFIABLE", "$", "No receipt")] };
        await fixture.Store.TrySaveAsync(state, null, Ct);
        var key = "recovery:" + request.ClientRequestId;
        await using (var db = fixture.CreateDbContext())
        {
            db.Calls.Add(new() { TenantId = "planning-tests", SessionId = "recovery", RequestHash = request.ClientRequestId, PayloadKey = key });
            await db.SaveChangesAsync(Ct);
        }
        await fixture.Records.UpsertAsync(PlanningBudgetSink.Collection, "planning-tests", "recovery",
            JsonSerializer.Serialize(new LLMUsageBudgetSnapshot { StartedAtUtc = DateTimeOffset.UtcNow, Calls = 4, InputTokens = 10, OutputTokens = 5, TotalTokens = 15, EstimatedCost = 0.01m, EstimatedCostCurrency = "EUR" }, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), EfPlanningSessionStore.Author, Ct);
        return state;
    }
    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null)
            => ((inputTokens ?? 0) + (outputTokens ?? 0)) * 0.0001m;
    }
    private sealed class Client : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject(), Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } }); }
    }
}
