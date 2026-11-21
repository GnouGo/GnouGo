using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningConstructionFinancialAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftThatCannotAffordItsRequiredReviewNeverDispatchesOrCharges(bool parentLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var parent = new LLMUsageBudgetScope(new() { MaxEstimatedCostUsd = 20_000 },
            sink: new PlanningBudgetSink(fixture.Records, "tenant", "financial"));
        var budget = parentLimit ? parent.CreateChild(new() { MaxEstimatedCostUsd = 100_000 }) : parent;
        var before = budget.Snapshot; var parentBefore = parent.Snapshot;
        var model = new Model(); var price = new Price();
        var journal = new PlanningModelJournal(model, fixture, fixture.Records, "tenant", "financial", budget, price);
        var engine = new WorkflowEngine { LLMClient = journal, LLMCapabilities = model, ModelUsageCostEstimator = price };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var state = new PlanningSession { Request = new()
        {
            TenantId = "tenant", SessionId = "financial", Prompt = "Return a greeting", MaxModelCalls = 12,
            Generation = new("medium", 32_000, 32_768),
            Options = new() { ["generator"] = new JsonObject { ["provider"] = "fixture", ["model"] = "priced" } }
        } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == ErrorCodes.LlmBudgetExceeded);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, result.ModelCalls);
        Assert.Null(result.PendingCall);
        Assert.Null(result.Plan);
        Assert.Equal(before, budget.Snapshot);
        Assert.Equal(parentBefore, parent.Snapshot);
        Assert.Contains((16_000L, 8_192L), price.Estimates);
        Assert.Contains((16_000L, 2_048L), price.Estimates);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(0, await db.Calls.CountAsync(ct));
        Assert.Null(await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "tenant", "financial", EfPlanningSessionStore.Author, ct));
    }

    private sealed class Price : IModelUsageCostEstimator
    {
        internal List<(long Input, long Output)> Estimates { get; } = [];
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null)
        { Estimates.Add((inputTokens ?? 0, outputTokens ?? 0)); return (inputTokens ?? 0) + (outputTokens ?? 0); }
    }
    private sealed class Model : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject() }); }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128_000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
    }
}
