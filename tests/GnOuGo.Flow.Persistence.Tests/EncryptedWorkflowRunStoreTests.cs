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
                Id = "/workflow/main/step/write", StepType = "mcp.call", Status = "completed", Output = new JsonObject { ["secret"] = secret }
            }, (_, existing) => existing);
            await owner.SaveAsync(Ct);
        }
        var restarted = Store();
        Assert.Equal(secret, (await restarted.ReadAsync("tenant", "run", Ct))!.Inputs!["secret"]!.GetValue<string>());
        await using (var index = Index())
        {
            Assert.Single(await index.Runs.Where(r => r.TenantId == "tenant").ToListAsync(Ct));
            await index.Database.ExecuteSqlRawAsync("DROP TABLE WorkflowRuns", Ct);
        }
        var listed = await restarted.ListAsync("tenant", Ct);
        Assert.Single(listed);
        await using (var rebuilt = Index()) Assert.Equal(1, (await rebuilt.Runs.SingleAsync(Ct)).Revision);
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
    public async Task TenantOwnershipAndIncompatibleEncryptedRecords_AreEnforcedWithoutMutation()
    {
        var store = Store();
        await store.CreateAsync(NewRun("private"), Ct);
        Assert.Null(await store.ReadAsync("other", "run", Ct));
        Assert.Empty(await store.ListAsync("other", Ct));
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => store.AcquireAsync("other", "run", 0, Ct));
        var records = new KeyVaultRecordStore(Path.Combine(_root, "vault.db"));
        const string legacy = "{\"schemaVersion\":8,\"private\":\"untouched\"}";
        await records.UpsertAsync(EncryptedWorkflowRunStore.Collection, "tenant", "old", legacy, "test", Ct);
        var error = await Assert.ThrowsAsync<WorkflowRunConflictException>(() => store.ReadAsync("tenant", "old", Ct));
        Assert.Contains("Regenerate and approve", error.Message);
        Assert.Equal(legacy, (await records.GetAsync(EncryptedWorkflowRunStore.Collection, "tenant", "old", "test", Ct))!.Value);
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
