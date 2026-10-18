using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningJudgeJournalAuditTests
{
    [Fact]
    public async Task UncertainJudgeCompletionRetainsSharedAccountingAndCannotRedispatch()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new Model { Fail = true };
        var limits = new LLMUsageBudgetLimits { MaxCalls = 12 };
        var budget = new LLMUsageBudgetScope(limits, sink: new PlanningBudgetSink(fixture.Records, "tenant", "uncertain-judge"));
        var request = new LLMRequest { Model = "fixture", Prompt = "PRIVATE_JUDGE_EVIDENCE" };
        PlanningPersistenceTests.Identify(request, "uncertain-judge");
        await Assert.ThrowsAsync<IOException>(() => new PlanningModelJournal(client, fixture, fixture.Records,
            "tenant", "uncertain-judge", budget, new Estimator()).CallAsync(request, Ct));
        var persisted = await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "tenant", "uncertain-judge", EfPlanningSessionStore.Author, Ct);
        var restored = new LLMUsageBudgetScope(limits, JsonSerializer.Deserialize(persisted!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => new PlanningModelJournal(client, fixture, fixture.Records,
            "tenant", "uncertain-judge", restored, new Estimator()).CallAsync(request, Ct));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code);
        Assert.Equal(1, restored.Snapshot.Calls);
        Assert.Equal(1, client.Calls);
        Assert.Null(await fixture.Records.GetAsync(PlanningModelJournal.Collection, "tenant", "uncertain-judge:" + request.ClientRequestId, EfPlanningSessionStore.Author, Ct));
    }

    [Fact]
    public async Task JudgeReceiptAndPrivateBookkeepingRecoverFromEncryptedStorageWithoutAnotherCall()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new Model();
        var limits = new LLMUsageBudgetLimits { MaxCalls = 12 };
        var budget = new LLMUsageBudgetScope(limits, sink: new PlanningBudgetSink(fixture.Records, "tenant", "judge-session"));
        var request = new LLMRequest { Model = "fixture", Prompt = "PRIVATE_JUDGE_EVIDENCE",
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"findings":{"type":"array","items":{"type":"string"}},"uncertainty":{"type":"string"}},"required":["findings","uncertainty"],"additionalProperties":false}""") };
        PlanningPersistenceTests.Identify(request, "judge-session");
        var state = new PlanningSession
        {
            IntentVersion = 3, ModelCalls = 1, ReplanAttempts = 0, Status = PlanningStatus.Stopped,
            Request = new() { TenantId = "tenant", SessionId = "judge-session", Prompt = "PRIVATE_ORIGINAL_REQUEST",
                Options = new() { ["business_judge"] = true, ["business_judge_reviews"] = new JsonArray(),
                    ["business_judge_authority"] = new JsonArray("/root/outputs/PRIVATE_AUTHORIZED_SLOT") } },
            PendingCall = new() { Id = request.ClientRequestId!, Purpose = "judge", Request = request },
            Diagnostics = [new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Interrupted after the durable response.")]
        };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "judge-session", budget, new Estimator());
        var first = await journal.CallAsync(request, Ct);

        var restored = (await fixture.Store.LoadAsync("tenant", "judge-session", Ct))!;
        await PlanningModelRecovery.ResumeAsync(restored, fixture.Records, request.ClientRequestId, Ct, limits);
        var restoredBudget = new LLMUsageBudgetScope(limits, restored.Usage);
        var replay = await new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "judge-session", restoredBudget,
            new Estimator(), capabilities: new UnavailableCapabilities()).CallAsync(restored.PendingCall!.Request, Ct);
        Assert.Equal(JsonSerializer.Serialize(first, PlanningJsonContext.Default.LLMResponse), JsonSerializer.Serialize(replay, PlanningJsonContext.Default.LLMResponse));
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, restored.ModelCalls);
        Assert.Equal(0, restored.ReplanAttempts);
        Assert.Equal(1, restoredBudget.Snapshot.Calls);
        Assert.Equal(10, restoredBudget.Snapshot.InputTokens);
        Assert.Equal(5, restoredBudget.Snapshot.OutputTokens);
        Assert.Equal("judge", restored.PendingCall.Purpose);
        Assert.Equal(request.ClientRequestId, restored.PendingCall.Id);
        Assert.Equal(PlanningStatus.Generating, restored.Status);
        Assert.Empty(restored.Diagnostics);
        Assert.Null(restored.ApprovedHash);
        Assert.Null(await fixture.Store.LoadAsync("other-tenant", "judge-session", Ct));
        foreach (var path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var persisted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Ct));
            Assert.DoesNotContain("PRIVATE_JUDGE_EVIDENCE", persisted);
            Assert.DoesNotContain("PRIVATE_ORIGINAL_REQUEST", persisted);
            Assert.DoesNotContain("PRIVATE_AUTHORIZED_SLOT", persisted);
            Assert.DoesNotContain("PRIVATE_JUDGE_UNCERTAINTY", persisted);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Model : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls;
        internal bool Fail;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new IOException("Uncertain completion.");
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(), ["uncertainty"] = "PRIVATE_JUDGE_UNCERTAINTY" },
                Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
    }
    private sealed class UnavailableCapabilities : ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => throw new InvalidOperationException("A retained receipt must not recheck admission.");
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("A retained receipt must not recheck admission.");
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("A retained receipt must not recheck admission.");
    }
    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0.001m;
    }
}
