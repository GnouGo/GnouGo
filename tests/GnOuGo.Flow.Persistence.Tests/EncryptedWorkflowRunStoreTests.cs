using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GnOuGo.Flow.Persistence.Tests;

public sealed class EncryptedWorkflowRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-journal-tests-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private EncryptedWorkflowRunStore Store() => new(new KeyVaultRecordStore(Path.Combine(_root, "vault.db")),
        Path.Combine(_root, "index.db"), Path.Combine(_root, "owners"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SplitHeaderRetainsCapturedBudgetPolicyAndUncertainAccountingAcrossEncryptedRestart(bool unlimited)
    {
        var run = NewRun("private budget input");
        var scope = unlimited ? LLMUsageBudgetScope.CreateTrackingOnly()
            : new LLMUsageBudgetScope(new() { MaxCalls = 2, MaxTotalTokens = 200000 });
        await scope.ReserveJournaledAttemptAsync("private-request", new() { Model = "deployment" }, null, 16000, 128000, [], "execution", Ct);
        run.ApplicationLlmBudgetMode = unlimited ? "unlimited" : "limited"; run.ApplicationLlmBudgetVersion = 1;
        run.ApplicationLlmBudgetOrigin = "operator_preference"; run.ModelUsage = scope.Snapshot;
        run.ModelBudget = unlimited ? null : scope.Limits;
        await Store().CreateAsync(run, Ct);
        var reopened = Store();
        await using (var lease = await reopened.AcquireAsync("tenant", "run", 0, Ct))
        {
            Assert.Equal(run.ApplicationLlmBudgetMode, lease.Run.ApplicationLlmBudgetMode);
            Assert.Equal(1, lease.Run.ApplicationLlmBudgetVersion); Assert.Equal("operator_preference", lease.Run.ApplicationLlmBudgetOrigin);
            Assert.Equal(run.ModelBudget, lease.Run.ModelBudget);
            Assert.Equal(1, lease.Run.ModelUsage!.UncertainCalls); Assert.False(lease.Run.ModelUsage.CostEstimateAvailable);
            Assert.Null(lease.Run.ModelUsage.JournaledAttempts!["private-request"].VerifiedUsage);
            lease.Run.Status = WorkflowRunStatus.Failed;
            await lease.SaveAsync(Ct);
            Assert.Equal(OwnerRetention.Reconciliation, lease.Run.Retention!.Phase);
        }
        var restored = (await Store().ReadAsync("tenant", "run", Ct))!;
        Assert.Equal(run.ApplicationLlmBudgetMode, restored.ApplicationLlmBudgetMode);
        Assert.Equal(1, restored.ModelUsage!.Calls); Assert.Equal(144000, restored.ModelUsage.TotalTokens);
        Assert.Equal("pricing_unavailable", restored.ModelUsage.CostUnavailableReason);
        Assert.Null(await Store().ReadAsync("other", "run", Ct));
    }

    [Fact]
    public async Task ImagesRemainEncryptedAndOwnedByOneRunAcrossRestart()
    {
        var bytes = Encoding.UTF8.GetBytes("unique-private-content-for-vision-test");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var store = Store();
        await store.CreateAsync(NewRun("vision"), Ct);
        IWorkflowImageStore first = store;
        await first.PutAsync("tenant", "run", sha, bytes, Ct);
        IWorkflowImageStore restarted = Store();
        Assert.Equal(bytes, await restarted.GetAsync("tenant", "run", sha, Ct));
        Assert.Null(await restarted.GetAsync("other", "run", sha, Ct));
        Assert.Null(await restarted.GetAsync("tenant", "another", sha, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => first.PutAsync("tenant", "run", sha, [0], Ct));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.GetAsync("tenant", "run", sha, cancelled.Token));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_root, "*.db*"))
            Assert.DoesNotContain(Convert.ToBase64String(bytes), Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
    }

    [Fact]
    public async Task MappingArtifactsAreEncryptedTenantScopedAndSurviveRestart()
    {
        IMappingArtifactStore first = Store();
        const string script = "({private_mapping_field:source.observed})";
        await first.WriteAsync("tenant", new("mapping-key", script, null, 1), Ct);
        IMappingArtifactStore restarted = Store();
        Assert.Equal(script, (await restarted.ReadAsync("tenant", "mapping-key", Ct))!.Script);
        Assert.Null(await restarted.ReadAsync("other", "mapping-key", Ct));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_root, "*.db*"))
            Assert.DoesNotContain(script, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
        await restarted.RemoveAsync("tenant", "mapping-key", Ct);
        Assert.Null(await first.ReadAsync("tenant", "mapping-key", Ct));
    }

    [Fact]
    public async Task EncryptedJournalSurvivesNewStore_AndIndexLoss()
    {
        var store = Store();
        const string secret = "private-workflow-payload-90ac26";
        await store.CreateAsync(NewRun(secret), Ct);
        await using (var owner = await store.AcquireAsync("tenant", "run", 0, Ct))
        {
            owner.Run.Invocations.AddOrUpdate("/workflow/main/step/write", new WorkflowInvocation
            {
                Id = "/workflow/main/step/write", StepType = "mcp.call", Description = "Literal ${metadata} " + secret, Status = "completed", Output = new JsonObject { ["secret"] = secret }
            }, (_, existing) => existing);
            await owner.SaveAsync(Ct);
        }
        var restarted = Store();
        Assert.Equal("Literal ${metadata} " + secret, (await restarted.ReadAsync("tenant", "run", Ct))!.Invocations["/workflow/main/step/write"].Description);
        Assert.Equal(secret, (await restarted.ReadAsync("tenant", "run", Ct))!.Inputs!["secret"]!.GetValue<string>());
        await using (var index = Index())
        {
            Assert.Single(await index.Runs.Where(r => r.TenantId == "tenant").ToListAsync(Ct));
            await index.Database.ExecuteSqlRawAsync("DROP TABLE WorkflowRuns", Ct);
        }
        var listed = await restarted.ListAsync("tenant", Ct);
        Assert.Single(listed);
        await using (var rebuilt = Index()) Assert.Equal(1, (await rebuilt.Runs.SingleAsync(Ct)).Revision);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_root, "*.db*"))
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
    }

    [Fact]
    public async Task ConcurrentOwnersConflict_AndCancellationMergesWithoutLostReceipt()
    {
        var first = Store();
        var second = Store();
        await first.CreateAsync(NewRun("payload"), Ct);
        await using (var owner = await first.AcquireAsync("tenant", "run", 0, Ct))
        {
            await Assert.ThrowsAsync<WorkflowRunConflictException>(() => second.AcquireAsync("tenant", "run", 0, Ct));
            await Assert.ThrowsAsync<WorkflowRunConflictException>(() => second.CancelAsync("tenant", "run", 2, Ct));
            await second.CancelAsync("tenant", "run", 0, Ct);
            owner.Run.Status = "needs_reconciliation";
            await owner.SaveAsync(Ct);
            Assert.True(owner.Run.CancelRequested);
            Assert.Equal(2, owner.Run.Revision);
        }
        await using var recovered = await second.AcquireAsync("tenant", "run", 2, Ct);
        Assert.Equal("needs_reconciliation", recovered.Run.Status);
        Assert.True(await recovered.IsCancellationRequestedAsync(Ct));
    }

    [Fact]
    public async Task TenantOwnershipIsEnforcedOnEveryStore()
    {
        var store = Store();
        await store.CreateAsync(NewRun("private"), Ct);
        Assert.Null(await store.ReadAsync("other", "run", Ct));
        Assert.Empty(await store.ListAsync("other", Ct));
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => store.AcquireAsync("other", "run", 0, Ct));

    }


    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("1.5")]
    [InlineData("2147483647")]
    public void InvalidConfiguredWindowsAreRejected(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => OwnerRetentionPolicy.Parse(value));
    }

    [Fact]
    public void RepeatedTerminalWritesPreserveTheFirstDeadline()
    {
        var clock = new RetentionClock();
        var retained = new OwnerRetentionPolicy(12, clock).Capture();
        var run = NewRun("terminal");
        run.Status = WorkflowRunStatus.Completed;
        var finished = retained.ForRun(run, clock.Now.AddHours(1), true);
        Assert.Equal(OwnerRetention.Terminal, finished.Phase);
        Assert.Equal(clock.Now.AddHours(1).AddDays(12), finished.DeadlineUtc);
        Assert.Equal(finished, finished.ForRun(run, clock.Now.AddDays(45), true));
        Assert.True(finished.IsValid);
    }

    private sealed class RetentionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task OwnerRetentionSurvivesColdReadProjectionRebuildAndRepeatedCheckpoints()
    {
        var clock = new RetentionClock();
        var keys = new KeyVaultRecordStore(Path.Combine(_root, "vault.db"));
        EncryptedWorkflowRunStore Open(int days) => new(keys, Path.Combine(_root, "index.db"), Path.Combine(_root, "owners"),
            null, new OwnerRetentionPolicy(days, clock));
        var run = NewRun("shared");
        var body = new JsonObject { ["retained"] = new string('x', 8192) };
        run.Invocations["a"] = new() { Id = "a", StepType = "set", DataBefore = body, DataAfter = body.DeepClone().AsObject() };
        await Open(30).CreateAsync(run, Ct);
        clock.Now = clock.Now.AddDays(2);
        await using (var lease = await Open(7).AcquireAsync("tenant", "run", 0, Ct))
        {
            lease.Run.Invocations["human"] = new() { Id = "human", StepType = "human.input", Status = "waiting_for_human", Recovery = StepRecovery.HumanInput };
            lease.Run.Status = WorkflowRunStatus.WaitingForHuman;
            lease.Run.Events.Add(new(clock.Now, "dispatched", "human"));
            await lease.SaveAsync(Ct);
        }
        var deadline = clock.Now.AddDays(30);
        clock.Now = clock.Now.AddDays(25);
        await using (var lease = await Open(1).AcquireAsync("tenant", "run", 1, Ct))
        {
            Assert.Equal(deadline, lease.Run.Retention!.DeadlineUtc);
            lease.Run.Events.Add(new(clock.Now, "model_usage", null));
            await lease.SaveAsync(Ct);
        }
        await using (var db = Index()) await db.Database.EnsureDeletedAsync(Ct);
        var verified = Assert.Single(await Open(1).ListAsync("tenant", Ct));
        Assert.Equal(30, verified.Retention!.ResumeDays);
        Assert.Equal(deadline, verified.Retention.DeadlineUtc);
        Assert.True(JsonNode.DeepEquals(body, verified.Invocations["a"].DataBefore));
        await using var index = Index();
        var projection = await index.Set<OwnerRetentionIndex>().SingleAsync(Ct);
        Assert.Equal(deadline.UtcTicks, projection.DeadlineUtcTicks);
        Assert.Equal(verified.Revision, projection.Revision);
        Assert.Null(await Open(1).ReadAsync("elsewhere", "run", Ct));
    }

    [Fact]
    public async Task TwoDayExpiryFencesResumeAndRecreationBeforeAndAfterCollection()
    {
        Assert.Equal(2, OwnerRetentionPolicy.Parse(null).OperationalDays);
        var clock = new RetentionClock();
        var keys = new KeyVaultRecordStore(Path.Combine(_root, "vault.db"));
        EncryptedWorkflowRunStore Open() => new(keys, Path.Combine(_root, "index.db"), Path.Combine(_root, "owners"), null, new(2, clock));
        var run = NewRun(new string('x', 10_000));
        run.Status = WorkflowRunStatus.Completed;
        await Open().CreateAsync(run, Ct);
        clock.Now = run.Retention!.DeadlineUtc!.Value.AddTicks(-1);
        Assert.NotNull(await Open().ReadAsync("tenant", "run", Ct));
        clock.Now = clock.Now.AddTicks(1);
        Assert.Null(await Open().ReadAsync("tenant", "run", Ct));
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => Open().AcquireAsync("tenant", "run", 0, Ct));
        await Open().SweepAsync("tenant", Ct);
        await Open().SweepAsync("tenant", Ct);
        Assert.Empty(await keys.ListAsync(EncryptedWorkflowRunStore.Collection, "tenant", "test", Ct));
        Assert.Empty(await keys.ListAsync("flow-execution-blocks-v1", "tenant", "test", Ct));
        Assert.Empty(await Open().ListAsync("tenant", Ct));
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => Open().CreateAsync(NewRun("same ID"), Ct));
    }

    [Fact]
    public async Task LiveLeaseSurvivesDeadlineAndUncertainReceiptRemainsPinned()
    {
        var clock = new RetentionClock();
        var keys = new KeyVaultRecordStore(Path.Combine(_root, "vault.db"));
        var store = new EncryptedWorkflowRunStore(keys, Path.Combine(_root, "index.db"), Path.Combine(_root, "owners"), null, new(2, clock));
        var run = NewRun("receipt");
        await store.CreateAsync(run, Ct);
        await using (var lease = await store.AcquireAsync("tenant", "run", 0, Ct))
        {
            clock.Now = clock.Now.AddDays(3);
            await store.SweepAsync("tenant", Ct);
            Assert.NotNull(await store.ReadAsync("tenant", "run", Ct));
            lease.Run.Status = WorkflowRunStatus.NeedsReconciliation;
            lease.Run.ModelUsage = new() { UncertainCalls = 1, EstimatedCost = 1.23m };
            await lease.SaveAsync(Ct);
        }
        clock.Now = clock.Now.AddDays(2);
        await store.SweepAsync("tenant", Ct);
        Assert.Null(await store.ReadAsync("tenant", "run", Ct));
        Assert.NotNull(await keys.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "run", "test", Ct));
        var fence = Assert.Single(await keys.ListAsync("flow-expired-executions-v1", "tenant", "test", Ct));
        Assert.True(JsonNode.Parse(fence.Value)!["pinned"]!.GetValue<bool>());
        Assert.Equal(1.23m, JsonNode.Parse(fence.Value)!["usage"]!["estimatedCost"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task InterruptedCollectionRecoversFromTheFenceWithoutReadingDeletedBlocks()
    {
        var clock = new RetentionClock();
        var keys = new KeyVaultRecordStore(Path.Combine(_root, "vault.db"));
        EncryptedWorkflowRunStore Open(IKeyVaultRecordStore vault) => new(vault, Path.Combine(_root, "index.db"),
            Path.Combine(_root, "owners"), null, new(2, clock));
        var run = NewRun(new string('x', 16384));
        run.Status = WorkflowRunStatus.Completed;
        await Open(keys).CreateAsync(run, Ct);
        clock.Now = clock.Now.AddDays(2);
        await Assert.ThrowsAsync<IOException>(() => Open(new InterruptedUnlink(keys)).SweepAsync("tenant", Ct));
        Assert.Null(await Open(keys).ReadAsync("tenant", "run", Ct));
        Assert.NotNull(await keys.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "run", "test", Ct));
        await Open(keys).SweepAsync("tenant", Ct);
        Assert.Empty(await keys.ListAsync("flow-execution-blocks-v1", "tenant", "test", Ct));
        Assert.Null(await keys.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "run", "test", Ct));
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => Open(keys).CreateAsync(NewRun("retry"), Ct));
    }

    private sealed class InterruptedUnlink(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) =>
            inner.GetAsync(collection, tenantId, key, author, ct);
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default) =>
            inner.UpsertAsync(collection, tenantId, key, value, author, ct);
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default) =>
            inner.ListAsync(collection, tenantId, author, ct);
        public async Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
        {
            var removed = await inner.DeleteAsync(collection, tenantId, key, author, ct);
            if (collection == "flow-execution-blocks-v1") throw new IOException("Interrupted after unlink.");
            return removed;
        }
    }

    private WorkflowRunDbContext Index() => new(new DbContextOptionsBuilder<WorkflowRunDbContext>()
        .UseSqlite("Data Source=" + Path.Combine(_root, "index.db")).UseModel(CompiledModels.WorkflowRunDbContextModel.Instance).Options);

    private static WorkflowRun NewRun(string payload) => new()
    {
        TenantId = "tenant", RunId = "run", Inputs = new JsonObject { ["secret"] = payload },
        Limits = new() { TenantId = "tenant", RunId = "run" }
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
