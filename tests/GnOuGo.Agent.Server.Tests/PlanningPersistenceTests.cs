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
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void RetiredInternalFormatsAreRejectedWithoutUpgradingTheirPayload(int format)
    {
        var state = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "retired", Prompt = "original" } };
        var json = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!;
        json["schemaVersion"] = format;
        var retained = json.ToJsonString();
        var error = Assert.Throws<PlanningConflictException>(() => PlanningSessionStorage.Read(retained, "tenant", "retired"));
        Assert.Contains("format 11", error.Message);
        Assert.Equal(retained, json.ToJsonString());
    }

    [Fact]
    public async Task RetiredCollectionIsNeverUsedAsAReaderOrFallback()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "retired", Prompt = "original" } };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        await using var db = fixture.CreateDbContext();
        var key = (await db.Sessions.SingleAsync(Ct)).PayloadKey;
        Assert.True(await fixture.Records.DeleteAsync(EfPlanningSessionStore.Collection, "tenant", key, "test", Ct));
        var payload = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!;
        payload["schemaVersion"] = 10;
        var archive = await fixture.Records.UpsertAsync("agent-planning-sessions-v10", "tenant", key, payload.ToJsonString(), "test", Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => fixture.Store.LoadAsync("tenant", "retired", Ct));
        Assert.Empty(await fixture.Store.ListAsync("tenant", Ct));
        Assert.Equal(archive, await fixture.Records.GetAsync("agent-planning-sessions-v10", "tenant", key, "test", Ct));
        Assert.Null(await fixture.Records.GetAsync(EfPlanningSessionStore.Collection, "tenant", key, "test", Ct));
    }

    [Fact]
    public async Task ExplicitSuspendedConsumersRecoverExactlyInsideTheEncryptedTenantSnapshot()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        const string secret = "PRIVATE_TYPED_CONSUMER_AND_ORIGINAL_EVIDENCE";
        var state = new PlanningSession
        {
            Request = new() { TenantId = "tenant", SessionId = "continuation", Prompt = secret },
            ModelCalls = 7, ReplanAttempts = 2,
            Construction = new() { Compact = new() { ResumingStepId = "continuation_0",
                SuspendedSteps = [new()
                {
                    Id = "continuation_0", Status = "resuming", SuspendedAtCheckpoint = 1, LastPrerequisiteCheckpoint = 3,
                    Consumer = new() { Id = "consumer", Scope = "/root", Objective = secret, RequirementIds = ["original"], ExpectedOutputs = [secret] },
                    Prerequisite = new() { Need = secret, ExpectedType = new() { Kind = "number", Nullable = true }, EvidenceAddresses = ["source:original"] },
                    SourceVersions = new() { ["original"] = "source-hash" }, EvidenceHashes = new() { ["source:original"] = "evidence-hash" },
                    ContractHashes = new() { ["exact-operation@1"] = "contract-hash" }, DependencyHashes = new() { ["accepted"] = "task-hash" }
                }] } },
            PendingCall = new() { Id = "exact-issued", Request = new() { Prompt = secret, StructuredOutputSchema = new JsonObject { ["type"] = "object" } } }
        };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var bytes = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var restored = await fixture.Store.LoadAsync("tenant", "continuation", Ct);
        Assert.Equal(bytes, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal("continuation_0", restored!.Construction!.Compact!.ResumingStepId);
        Assert.Equal("number", Assert.Single(restored.Construction.Compact.SuspendedSteps!).Prerequisite.ExpectedType.Kind);
        Assert.Null(await fixture.Store.LoadAsync("another", "continuation", Ct));
        foreach (var file in fixture.GetPhysicalFiles())
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
        state.Construction.Compact.SuspendedSteps = null; state.Construction.Compact.ResumingStepId = null;
        var withoutContinuation = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.DoesNotContain("\"suspendedSteps\"", withoutContinuation); Assert.DoesNotContain("\"resumingStepId\"", withoutContinuation);
    }

    [Fact]
    public async Task TargetedRevisionAuthorityAndBaselineRemainEncryptedAndTenantOwned()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var plan = new TaskPlan { Root = new() { Outputs = [new("private_edit_scope", new() { Kind = "string", Text = "PRIVATE_RETAINED_WORK" })] } };
        var state = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "targeted", Prompt = "Correct selected output", Baseline = plan },
            Plan = plan, ModelCalls = 3, ReplanAttempts = 1, EditablePaths = ["/root/outputs/private_edit_scope"], RevisionScope = ["/root/outputs/private_edit_scope"] };
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var restored = await fixture.Store.LoadAsync("tenant", "targeted", Ct);
        Assert.NotNull(restored); Assert.Equal(state.EditablePaths, restored.EditablePaths); Assert.Equal(state.RevisionScope, restored.RevisionScope);
        Assert.Equal(3, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts);
        Assert.Equal("PRIVATE_RETAINED_WORK", restored.Plan!.Root.Outputs[0].Value.Text);
        Assert.Equal("PRIVATE_RETAINED_WORK", restored.Request.Baseline!.Root.Outputs[0].Value.Text);
        Assert.Null(await fixture.Store.LoadAsync("other", "targeted", Ct));
        foreach (var file in fixture.GetPhysicalFiles())
        {
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct));
            Assert.DoesNotContain("PRIVATE_RETAINED_WORK", bytes); Assert.DoesNotContain("/root/outputs/private_edit_scope", bytes);
        }
    }

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
        foreach (var file in fixture.GetPhysicalFiles())
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
    private sealed class CountingClient : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium", "low"]);

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
    [Fact]
    public async Task HistoricSessionKeepsItsIndexAuthorityAndCannotBeRecreatedFromOrphanedRevisions()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var state = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "old" } };
        // A different owner can contain the same prefix and a numeric segment in its ID.
        var sibling = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "prefix:1:sibling" } };
        Assert.True(await fixture.Store.TrySaveAsync(sibling, null, Ct));
        Assert.True(await fixture.Store.TrySaveAsync(new() { Request = new() { TenantId = "tenant", SessionId = "prefix" } }, null, Ct));
        await fixture.Records.UpsertAsync(EfPlanningSessionStore.Collection, "tenant", "old:0:historic",
            JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), EfPlanningSessionStore.Author, Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => fixture.Store.TrySaveAsync(state, null, Ct));
        await using (var db = fixture.CreateDbContext())
        {
            db.Sessions.Add(new() { TenantId = "tenant", SessionId = "old", PayloadKey = "old:0:historic", Status = state.Status,
                UpdatedAtTicks = state.UpdatedAtUtc.UtcTicks });
            await db.SaveChangesAsync(Ct);
        }
        var restored = (await fixture.Store.LoadAsync("tenant", "old", Ct))!;
        Assert.Null(restored.Retention);
        restored.Revision++;
        Assert.True(await fixture.Store.TrySaveAsync(restored, 0, Ct));
        Assert.Null((await fixture.Store.LoadAsync("tenant", "old", Ct))!.Retention);
        Assert.Null(await fixture.Records.GetAsync(EfPlanningSessionStore.Heads, "tenant", "old", EfPlanningSessionStore.Author, Ct));
    }

    [Fact]
    public async Task OnlyPlanningProgressAdvancesTheActiveCrashWindow()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var clock = new RetentionClock();
        var store = new EfPlanningSessionStore(fixture, fixture.Records, new(30, clock));
        var state = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "active" } };
        Assert.True(await store.TrySaveAsync(state, null, Ct));
        var captured = state.Retention!;
        clock.Now = clock.Now.AddDays(1);
        state.Revision++; state.UpdatedAtUtc = clock.Now; state.ActiveMilliseconds += 5;
        Assert.True(await store.TrySaveAsync(state, 0, Ct));
        Assert.Equal(captured, state.Retention);
        Assert.Null(state.Retention!.DeadlineUtc);
        state.Revision++; state.ModelCalls++; state.PendingCall = new() { Id = "issued" };
        Assert.True(await store.TrySaveAsync(state, 1, Ct));
        Assert.Equal(clock.Now.AddDays(30), state.Retention!.CrashResumeUntilUtc);
        Assert.Equal(captured.EnteredUtc, state.Retention.EnteredUtc);
    }

    private sealed class RetentionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task CommittedPlanningHeadRestoresTheExactWaitAfterLosingItsIndex()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var clock = new RetentionClock();
        EfPlanningSessionStore Open(int days) => new(fixture, fixture.Records, new(days, clock));
        var owner = new PlanningSession { Request = new() { TenantId = "tenant", SessionId = "plan" }, Yaml = "name: retained" };
        Assert.True(await Open(30).TrySaveAsync(owner, null, Ct));
        var hash = Assert.IsType<string>(owner.ComputeArtifactHash());
        clock.Now = clock.Now.AddHours(6);
        owner.Revision++; owner.Status = PlanningStatus.Clarification;
        owner.PendingQuestions = [new("q", "Continue?", [], null)];
        Assert.True(await Open(7).TrySaveAsync(owner, 0, Ct));
        var waited = owner.Retention!;
        Assert.Equal(clock.Now.AddDays(30), waited.DeadlineUtc);
        clock.Now = clock.Now.AddDays(10);
        owner.Revision++; owner.UpdatedAtUtc = clock.Now; owner.WaitingSinceUtc = clock.Now;
        Assert.True(await Open(7).TrySaveAsync(owner, 1, Ct));
        Assert.Equal(waited, owner.Retention);
        // A higher, immutable but unpublished candidate cannot become the winner during recovery.
        await fixture.Records.UpsertAsync(EfPlanningSessionStore.Collection, "tenant", "plan:managed:99:orphan",
            "unpublished", EfPlanningSessionStore.Author, Ct);
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync(Ct);
            await db.Database.EnsureCreatedAsync(Ct);
        }
        var reopened = Open(1);
        Assert.False(await reopened.TrySaveAsync(new() { Request = owner.Request }, null, Ct));
        await reopened.RebuildIndexAsync("tenant", Ct);
        var restored = Assert.Single(await reopened.ListAsync("tenant", Ct));
        Assert.Equal(2, restored.Revision);
        Assert.Equal(waited, restored.Retention);
        Assert.Equal(hash, restored.ComputeArtifactHash());
        await using var index = fixture.CreateDbContext();
        var row = await index.Set<GnOuGo.Flow.Persistence.OwnerRetentionIndex>().SingleAsync(Ct);
        Assert.Equal(restored.Revision, row.Revision);
        Assert.Equal(waited.DeadlineUtc!.Value.UtcTicks, row.DeadlineUtcTicks);
        Assert.Null(await reopened.LoadAsync("someone-else", "plan", Ct));

        var other = (await reopened.LoadAsync("tenant", "plan", Ct))!;
        owner.Revision++; other.Revision++;
        owner.Status = PlanningStatus.Saved; other.Status = PlanningStatus.Cancelled;
        var commits = await Task.WhenAll(Open(7).TrySaveAsync(owner, 2, Ct), Open(1).TrySaveAsync(other, 2, Ct));
        Assert.Single(commits, value => value);
        Assert.Equal(clock.Now.AddDays(30), (await reopened.LoadAsync("tenant", "plan", Ct))!.Retention!.DeadlineUtc);
    }

    internal sealed class StoreFixture(string root, bool retain = false) : IDbContextFactory<PlanningDbContext>, IAsyncDisposable
    {
        public string Root { get; } = root;
        public IKeyVaultRecordStore Records { get; } = KeyVaultRecordStoreFactory.CreateWorkspaceStore(Path.Combine(root, "vault.db"), root);
        public EfPlanningSessionStore Store => new(this, Records);
        public string[] GetPhysicalFiles()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return Directory.GetFiles(Root, "*", SearchOption.AllDirectories);
        }
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
