using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningModelRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LLMUsageBudgetLimits Limits = new() { MaxCalls = 8, MaxTotalTokens = 1_000_000 };

    [Fact]
    public async Task UncertainRequestNeverGetsAnotherIdentityOrAReleasedReservation()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var budget = await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "planning-tests", "recovery", "test", Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => PlanningModelRecovery.PrepareAsync(state, fixture.Records,
            Limits, new Estimator(), new TestExchangeRateProvider(), Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(budget, await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "planning-tests", "recovery", "test", Ct));
        Assert.Equal(1, state.ModelCalls);
        Assert.Null(Assert.Single(state.Usage!.JournaledAttempts!).Value.VerifiedUsage);
    }

    [Fact]
    public async Task RejectedRequestCannotReserveOrRetryAfterRecovery()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        state.Diagnostics = [new("MODEL_REQUEST_REJECTED", "/", "InvalidRequest (HTTP 400)")];
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var error = await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningModelRecovery.PrepareAsync(state, fixture.Records,
            Limits, new Estimator(), new TestExchangeRateProvider(), Ct));
        Assert.Contains("rejected", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing_receipt")]
    [InlineData("stale_identity")]
    [InlineData("changed_schema")]
    [InlineData("foreign_tenant")]
    [InlineData("retired_format")]
    public async Task CurrentSavedResponseUsesExactAuthorityAndRestoresPhysicalAccounting(string? invalid)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        var pending = state.PendingCall!;
        var key = state.Request.SessionId + ":" + pending.Id;
        if (invalid != "missing_receipt")
            await fixture.Records.UpsertAsync(PlanningModelJournal.Collection, "planning-tests", key,
                JsonSerializer.Serialize(new LLMResponse { Json = new JsonObject { ["answer"] = "retained" } }, PlanningJsonContext.Default.LLMResponse), "test", Ct);
        if (invalid == "changed_schema") pending.Request.StructuredOutputSchema!["properties"]!["answer"]!["type"] = "number";
        if (invalid == "foreign_tenant") state.Request.TenantId = "other";
        if (invalid == "retired_format") state.SchemaVersion = 10;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        if (invalid is not null)
        {
            await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningModelRecovery.ResumeAsync(state, fixture.Records,
                invalid == "stale_identity" ? "different" : pending.Id, Ct, Limits));
            Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
            return;
        }
        await PlanningModelRecovery.ResumeAsync(state, fixture.Records, pending.Id, Ct, Limits);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(1, state.Usage!.Calls);
        Assert.Equal(pending.Id, state.PendingCall!.Id);
        Assert.Empty(state.Diagnostics); Assert.Null(state.ApprovedHash);
        var restored = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        state.Status = PlanningStatus.Stopped;
        await PlanningModelRecovery.ResumeAsync(state, fixture.Records, pending.Id, Ct, Limits);
        Assert.Equal(restored, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(1, state.ModelCalls);
    }

    [Fact]
    public async Task MissingPhysicalAttemptAccountingCannotBecomeAReceiptRecovery()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var state = await SeedAsync(fixture);
        var key = state.Request.SessionId + ":" + state.PendingCall!.Id;
        await fixture.Records.UpsertAsync(PlanningModelJournal.Collection, "planning-tests", key,
            JsonSerializer.Serialize(new LLMResponse { Json = new JsonObject { ["answer"] = "retained" } }, PlanningJsonContext.Default.LLMResponse), "test", Ct);
        var usage = state.Usage! with { JournaledAttempts = null };
        await fixture.Records.UpsertAsync(PlanningBudgetSink.Collection, "planning-tests", "recovery",
            JsonSerializer.Serialize(usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), "test", Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningModelRecovery.ResumeAsync(state, fixture.Records, state.PendingCall.Id, Ct, Limits));
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.NotNull(state.PendingCall);
    }

    private static async Task<PlanningSession> SeedAsync(PlanningPersistenceTests.StoreFixture fixture)
    {
        var state = new PlanningSession
        {
            Request = new() { TenantId = "planning-tests", SessionId = "recovery", Prompt = "PRIVATE_RETRY_PROMPT" },
            Construction = new() { Limits = new() { PhasePolicy = new()
            {
                Navigation = new() { TargetInputTokens = 512, MaxInputTokens = 4096, OutputTokens = 64 },
                Draft = new() { TargetInputTokens = 512, MaxInputTokens = 4096, OutputTokens = 64 },
                Review = new() { TargetInputTokens = 512, MaxInputTokens = 4096, OutputTokens = 64 }
            } } }
        };
        PlanningSessionCreation.Capture(state);
        var request = PlanningConstructionPhasePolicies.Apply(new LLMRequest
        {
            Provider = "fixture", Model = "fixture", Prompt = state.Request.Prompt, StructuredOutputStrict = true, RequiredReviewCalls = 1,
            StructuredOutputSchema = JsonNode.Parse("""{"title":"construction_draft","type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""")
        }, state.Request, "construction_draft");
        request.ClientRequestId = "recovery:1:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        state.ModelCalls = 1; state.ActiveMilliseconds = 100; state.Status = PlanningStatus.Stopped;
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = "construction_draft", Request = request };
        state.Diagnostics = [new("MODEL_DISPATCH_UNVERIFIABLE", "/", "No receipt")];
        var budget = new LLMUsageBudgetScope(Limits, sink: new PlanningBudgetSink(fixture.Records, "planning-tests", "recovery"));
        await budget.ReserveJournaledAttemptAsync(request.ClientRequestId + ":1", request, new Estimator(), 4096, 64, [], "planning", Ct);
        state.Usage = budget.Snapshot;
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        await fixture.Records.UpsertAsync(PlanningModelJournal.RequestCollection, "planning-tests", "recovery:" + request.ClientRequestId,
            JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "test", Ct);
        return state;
    }

    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0;
    }
}
