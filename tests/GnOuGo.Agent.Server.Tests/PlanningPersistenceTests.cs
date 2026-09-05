using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningPersistenceTests
{
    [Fact]
    public async Task EncryptedRevisions_SurviveReopen_RejectStaleWrites_AndIsolateTenants()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var state = new PlanningSnapshot { Request = new() { TenantId = "one", SessionId = "same", Prompt = "PRIVATE_PLANNING_CONTENT_81352" } };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        state.Revision = 1; state.Status = PlanningStatus.BehaviorReview;
        Assert.True(await fixture.Store.TrySaveAsync(state, 0, Ct));
        state.Revision = 2;
        Assert.False(await fixture.Store.TrySaveAsync(state, 0, Ct));
        var reopened = new EfPlanningSessionStore(fixture, fixture.Records);
        var restored = await reopened.LoadAsync("one", "same", Ct);
        Assert.Equal(1, restored!.Revision);
        Assert.Equal(state.Request.Prompt, restored.Request.Prompt);
        Assert.Null(await reopened.LoadAsync("two", "same", Ct));
        state.Request.TenantId = "two"; state.Revision = 0;
        Assert.True(await reopened.TrySaveAsync(state, null, Ct));
        Assert.Single(await reopened.ListAsync("one", Ct));
        Assert.Single(await reopened.ListAsync("two", Ct));
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_PLANNING_CONTENT_81352", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
    }

    [Fact]
    public async Task ConcurrentRevisions_OnlyOneWriterWins()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var state = new PlanningSnapshot { Request = new() { TenantId = "tenant", SessionId = "session", Prompt = "request" } };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var one = (await fixture.Store.LoadAsync("tenant", "session", Ct))!;
        var two = (await fixture.Store.LoadAsync("tenant", "session", Ct))!;
        one.Revision = two.Revision = 1;
        one.Status = PlanningStatus.Cancelled; two.Status = PlanningStatus.Failed;
        var results = await Task.WhenAll(fixture.Store.TrySaveAsync(one, 0, Ct), fixture.Store.TrySaveAsync(two, 0, Ct));
        Assert.Single(results, result => result);
        Assert.Equal(1, (await fixture.Store.LoadAsync("tenant", "session", Ct))!.Revision);
    }

    [Fact]
    public async Task ModelReceipt_ReplaysWithoutDispatchOrDoubleCounting()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient();
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 5 });
        var request = new LLMRequest { Model = "fake", Prompt = "PRIVATE_MODEL_REQUEST", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", 0, budget, new FakeEstimator());
        var original = await journal.CallAsync(request, Ct);
        var calls = budget.Snapshot.Calls;
        var reopened = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", 0, budget, new FakeEstimator());
        Assert.Equal(original.Text, (await reopened.CallAsync(request, Ct)).Text);
        Assert.Equal(1, client.Calls);
        Assert.Equal(calls, budget.Snapshot.Calls);
    }

    [Fact]
    public async Task UnknownRequestReceipt_IsNotSilentlyDispatchedAgain()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient { Fail = true };
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 5 });
        var request = new LLMRequest { Model = "fake", Prompt = "request" };
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", 0, budget, new FakeEstimator());
        await Assert.ThrowsAnyAsync<Exception>(() => journal.CallAsync(request, Ct));
        var reopened = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", 0, budget, new FakeEstimator());
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.CallAsync(request, Ct));
        Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal(1, client.Calls);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class CountingClient : ILLMClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new IOException("Simulated connection loss");
            return Task.FromResult(new LLMResponse { Text = "PRIVATE_MODEL_RESPONSE", Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
    private sealed class FakeEstimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0;
    }
    internal sealed class StoreFixture(string root) : IDbContextFactory<PlanningDbContext>, IAsyncDisposable
    {
        public string Root { get; } = root;
        public IKeyVaultRecordStore Records { get; } = KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(root, "vault.db"), root);
        public EfPlanningSessionStore Store => new(this, Records);
        public PlanningDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PlanningDbContext>().UseSqlite("Data Source=" + Path.Combine(Root, "planning.db") + ";Pooling=False").Options);
        public static async Task<StoreFixture> CreateAsync()
        {
            var fixture = new StoreFixture(Path.Combine(Path.GetTempPath(), "GnOuGo.Planning.Tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture.Root);
            await using var db = fixture.CreateDbContext();
            await db.Database.EnsureCreatedAsync(Ct);
            return fixture;
        }
        public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
