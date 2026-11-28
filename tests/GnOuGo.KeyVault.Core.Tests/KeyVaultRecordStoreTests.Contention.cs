using System.Diagnostics;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Data.Sqlite;

namespace GnOuGo.KeyVault.Core.Tests;

public sealed partial class KeyVaultRecordStoreTests
{
    [Fact]
    public async Task BusyAuditRetriesAfterReleasingTheSnapshotAndWritesExactlyOneAudit()
    {
        using var db = new TemporaryKeyVaultDatabase();
        var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        await store.UpsertAsync(Collection, "tenant", "request", new string('x', 100000), Author, ct);
        await using var blocker = db.CreateConnection();
        await blocker.OpenAsync(ct);
        await using var snapshot = blocker.BeginTransaction(deferred: true);
        await using var hold = blocker.CreateCommand();
        hold.Transaction = snapshot;
        hold.CommandText = "SELECT COUNT(*) FROM KeyVaultRecordAuditEntries;";
        await hold.ExecuteScalarAsync(ct);
        var read = Task.Run(() => store.GetAsync(Collection, "tenant", "request", Author, ct), ct);
        await Task.Delay(1400, ct); // Longer than one provider busy attempt.
        Assert.False(read.IsCompleted);
        await snapshot.CommitAsync(ct);
        Assert.Equal(new string('x', 100000), (await read)!.Value);
        await using var check = blocker.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM KeyVaultRecordAuditEntries WHERE Operation='GetRecord';";
        Assert.Equal(1L, await check.ExecuteScalarAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentContentionIsBoundedOrCancelledWithoutPublishingPartialWork(bool cancel)
    {
        using var db = new TemporaryKeyVaultDatabase();
        var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        await store.UpsertAsync(Collection, "tenant", "budget", "retained-reservation", Author, ct);
        await using var blocker = db.CreateConnection();
        await blocker.OpenAsync(ct);
        await using var transaction = blocker.BeginTransaction();
        await using var update = blocker.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE KeyVaultRecords SET UpdatedBy='blocker';";
        await update.ExecuteNonQueryAsync(ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cancel) cancellation.CancelAfter(150);
        var clock = Stopwatch.StartNew();
        var write = Task.Run(() => store.UpsertAsync(Collection, "tenant", "budget", new string('z', 100000), Author, cancellation.Token), ct);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        else
        {
            var error = await Assert.ThrowsAsync<KeyVaultAccessException>(() => write);
            Assert.True(error.IsTransient);
            Assert.IsType<SqliteException>(error.InnerException);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(cancel ? 3 : 10), clock.Elapsed.ToString());
        await transaction.RollbackAsync(ct);
        Assert.Equal("retained-reservation", (await new KeyVaultRecordStore(db.DatabasePath).GetAsync(Collection, "tenant", "budget", Author, ct))!.Value);
    }

    [Fact]
    public async Task IndependentStoresContendAcrossAdmissionSessionInspectionAuditAndCancellation()
    {
        using var db = new TemporaryKeyVaultDatabase();
        var ct = TestContext.Current.CancellationToken;
        var seed = new KeyVaultRecordStore(db.DatabasePath);
        await seed.UpsertAsync("requests", "tenant", "session:9", "exact-issued-request", Author, ct);
        await seed.UpsertAsync("budgets", "tenant", "session", "uncertain-reservation", Author, ct);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = Enumerable.Range(0, 5).Select(role => Task.Run(async () =>
        {
            var store = new KeyVaultRecordStore(db.DatabasePath);
            await start.Task;
            for (var i = 0; i < 12; i++)
                switch (role)
                {
                    case 0: await store.UpsertAsync("budgets", "tenant", "session", "uncertain-reservation", Author, ct); break;
                    case 1: await store.UpsertAsync("sessions", "tenant", "session", new string('s', 200000), Author, ct); break;
                    case 2: Assert.NotNull(await store.GetAsync("requests", "tenant", "session:9", Author, ct)); break;
                    case 3: await store.QueryMetadataAsync("sessions", "tenant", "session", Author, ct: ct); break;
                    case 4: await store.GetAsync("cancellations", "tenant", "session", Author, ct); break;
                }
        }, ct)).ToArray();
        start.SetResult();
        await Task.WhenAll(jobs);
        var cold = new KeyVaultRecordStore(db.DatabasePath);
        Assert.Equal("exact-issued-request", (await cold.GetAsync("requests", "tenant", "session:9", Author, ct))!.Value);
        Assert.Equal("uncertain-reservation", (await cold.GetAsync("budgets", "tenant", "session", Author, ct))!.Value);
        Assert.Equal(200000, (await cold.GetMetadataAsync("sessions", "tenant", "session", Author, ct))!.Characters);
    }
}
