using System.Text;
using System.Text.Json;
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
    public async Task ModelReceipt_ReplaysWithoutDispatchOrDoubleCounting()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient();
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 5 });
        var request = new LLMRequest { Model = "fake", Prompt = "PRIVATE_MODEL_REQUEST", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        Identify(request, "session");
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator());
        var original = await journal.CallAsync(request, Ct);
        var calls = budget.Snapshot.Calls;
        var reopened = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator());
        Assert.Equal(original.Text, (await reopened.CallAsync(request, Ct)).Text);
        Assert.Equal(1, client.Calls);
        Assert.Equal(calls, budget.Snapshot.Calls);
        await using var db = fixture.CreateDbContext();
        var key = (await db.Calls.SingleAsync(Ct)).PayloadKey;
        var encryptedRequest = await fixture.Records.GetAsync(PlanningModelJournal.RequestCollection, "tenant", key, EfPlanningSessionStore.Author, Ct);
        Assert.Contains(request.Prompt, encryptedRequest!.Value);
        foreach (var file in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct));
            Assert.DoesNotContain("PRIVATE_MODEL_REQUEST", bytes);
            Assert.DoesNotContain("PRIVATE_MODEL_RESPONSE", bytes);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task UnknownRequestReceipt_IsNotSilentlyDispatchedAgain(int? statusCode)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient { Fail = true, FailureStatusCode = statusCode };
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 5 },
            sink: new PlanningBudgetSink(fixture.Records, "tenant", "session"));
        var request = new LLMRequest { Model = "fake", Prompt = "request" };
        Identify(request, "session");
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator());
        await Assert.ThrowsAnyAsync<Exception>(() => journal.CallAsync(request, Ct));
        var persisted = await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "tenant", "session", EfPlanningSessionStore.Author, Ct);
        var restoredBudget = new LLMUsageBudgetScope(new() { MaxCalls = 5 },
            JsonSerializer.Deserialize(persisted!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        var reopened = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", restoredBudget, new FakeEstimator());
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.CallAsync(request, Ct));
        Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, restoredBudget.Snapshot.Calls);
        await using var db = fixture.CreateDbContext();
        var reservation = await db.Calls.SingleAsync(Ct);
        Assert.Equal("reserved", reservation.Status);
        Assert.Null(await fixture.Records.GetAsync(PlanningModelJournal.Collection, "tenant", reservation.PayloadKey, EfPlanningSessionStore.Author, Ct));
    }

    [Fact]
    public async Task PersistedReceiptCompletesAnInterruptedIndexCommitWithoutRedispatch()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient(); var budget = new LLMUsageBudgetScope(new() { MaxCalls = 1 });
        var request = new LLMRequest { Model = "fake", Prompt = "request" }; Identify(request, "session");
        await new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator()).CallAsync(request, Ct);
        await using (var db = fixture.CreateDbContext()) { var row = await db.Calls.SingleAsync(Ct); row.Status = "reserved"; await db.SaveChangesAsync(Ct); }
        await new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator()).CallAsync(request, Ct);
        Assert.Equal(1, client.Calls); Assert.Equal(1, budget.Snapshot.Calls);
        await using var reopened = fixture.CreateDbContext(); Assert.Equal("completed", (await reopened.Calls.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task DiagnosticSixteenDispatchBudgetPersistsAndReplaysWithoutAnotherDispatch()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient(); var options = new LLMUsageBudgetLimits { MaxCalls = 16 };
        var budget = new LLMUsageBudgetScope(options, sink: new PlanningBudgetSink(fixture.Records, "tenant", "session"));
        var requests = Enumerable.Range(1, 17).Select(i => { var r = new LLMRequest { Model = "fake", Prompt = "decision " + i }; Identify(r, "session", i); return r; }).ToArray();
        var journal = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator());
        foreach (var request in requests.Take(16)) await journal.CallAsync(request, Ct);
        var saved = await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "tenant", "session", EfPlanningSessionStore.Author, Ct);
        var restored = new LLMUsageBudgetScope(options, JsonSerializer.Deserialize(saved!.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        var reopened = new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", restored, new FakeEstimator());
        await reopened.CallAsync(requests[0], Ct);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => reopened.CallAsync(requests[16], Ct));
        Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal(16, client.Calls); Assert.Equal(16, restored.Snapshot.Calls);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotExceedTheSharedCallBudget()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var client = new CountingClient(); var budget = new LLMUsageBudgetScope(new() { MaxCalls = 1 });
        var requests = Enumerable.Range(1, 4).Select(i => { var r = new LLMRequest { Model = "fake", Prompt = "request " + i }; Identify(r, "session", i); return r; }).ToArray();
        var results = await Task.WhenAll(requests.Select(async request =>
        {
            try { await new PlanningModelJournal(client, fixture, fixture.Records, "tenant", "session", budget, new FakeEstimator()).CallAsync(request, Ct); return true; }
            catch (WorkflowRuntimeException error) when (error.Code == GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded) { return false; }
        }));
        Assert.Single(results, success => success); Assert.Equal(1, client.Calls); Assert.Equal(1, budget.Snapshot.Calls);
        await using var db = fixture.CreateDbContext(); Assert.Equal(4, await db.Calls.CountAsync(Ct));
    }

    internal static void Identify(LLMRequest request, string session, int attempt = 1)
    {
        PlanningGenerationPolicy.Apply(request, new());
        request.ClientRequestId = null;
        var hash = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        request.ClientRequestId = session + ":" + attempt + ":construction:main:" + hash;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class CountingClient : ILLMClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public int? FailureStatusCode { get; init; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (FailureStatusCode is { } statusCode)
                throw new LLMClientException(LLMClientFailureKind.ServiceUnavailable, "Provider unavailable", retryable: true, statusCode: statusCode);
            if (Fail) throw new IOException("Simulated connection loss");
            return Task.FromResult(new LLMResponse { Text = "PRIVATE_MODEL_RESPONSE", Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
    private sealed class FakeEstimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0;
    }
    internal sealed class StoreFixture(string root, bool retain = false) : IDbContextFactory<PlanningDbContext>, IAsyncDisposable
    {
        public string Root { get; } = root;
        public IKeyVaultRecordStore Records { get; } = KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(root, "vault.db"), root);
        public EfPlanningSessionStore Store => new(this, Records);
        public PlanningDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PlanningDbContext>().UseSqlite("Data Source=" + Path.Combine(Root, "planning.db") + ";Pooling=False").Options);
        public static async Task<StoreFixture> CreateAsync(string? retainedDirectory = null)
        {
            var fixture = new StoreFixture(retainedDirectory ?? Path.Combine(Path.GetTempPath(), "GnOuGo.Planning.Tests", Guid.NewGuid().ToString("N")), retainedDirectory is not null);
            Directory.CreateDirectory(fixture.Root);
            await using var db = fixture.CreateDbContext();
            await db.Database.EnsureCreatedAsync(Ct);
            return fixture;
        }
        public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (!retain) Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
