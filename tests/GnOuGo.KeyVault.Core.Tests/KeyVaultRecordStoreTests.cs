using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.KeyVault.Core.Tests;

public sealed class KeyVaultRecordStoreTests
{
    private const string Collection = "application.records";
    private const string Author = "generic-consumer";

    [Fact]
    public async Task Store_EncryptsValuesAndWritesAuditEntries()
    {
        using var database = new TemporaryKeyVaultDatabase();
        IKeyVaultRecordStore store = new KeyVaultRecordStore(database.DatabasePath);

        var saved = await store.UpsertAsync(
            Collection,
            "tenant-a",
            "agent-hash",
            "sensitive-grant-payload",
            Author,
            TestContext.Current.CancellationToken);
        var loaded = await store.GetAsync(
            Collection,
            "tenant-a",
            "agent-hash",
            Author,
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("sensitive-grant-payload", loaded.Value);
        Assert.Equal(saved.CreatedAt, loaded.CreatedAt);

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var valueCommand = connection.CreateCommand())
        {
            valueCommand.CommandText = "SELECT EncryptedValue FROM KeyVaultRecords LIMIT 1;";
            var encrypted = Assert.IsType<string>(
                await valueCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.NotEqual("sensitive-grant-payload", encrypted);
            Assert.DoesNotContain("sensitive-grant-payload", encrypted, StringComparison.Ordinal);
        }

        await using var auditCommand = connection.CreateCommand();
        auditCommand.CommandText = """
            SELECT COUNT(*)
            FROM KeyVaultRecordAuditEntries
            WHERE Collection = $collection
              AND TenantId = $tenantId
              AND RecordKey = $recordKey
              AND Author = $author
              AND Operation IN ('UpsertRecord', 'GetRecord');
            """;
        auditCommand.Parameters.AddWithValue("$collection", Collection);
        auditCommand.Parameters.AddWithValue("$tenantId", "tenant-a");
        auditCommand.Parameters.AddWithValue("$recordKey", "agent-hash");
        auditCommand.Parameters.AddWithValue("$author", Author);
        Assert.Equal(
            2L,
            Assert.IsType<long>(await auditCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Store_IsolatesTenantsAndSupportsListAndDelete()
    {
        using var database = new TemporaryKeyVaultDatabase();
        IKeyVaultRecordStore store = new KeyVaultRecordStore(database.DatabasePath);
        var ct = TestContext.Current.CancellationToken;

        await store.UpsertAsync(Collection, "tenant-a", "agent-a", "value-a", Author, ct);
        await store.UpsertAsync(Collection, "tenant-b", "agent-a", "value-b", Author, ct);
        await store.UpsertAsync(Collection, "tenant-a", "agent-b", "value-c", Author, ct);

        var tenantARecords = await store.ListAsync(Collection, "tenant-a", Author, ct);
        Assert.Equal(2, tenantARecords.Count);
        Assert.All(tenantARecords, record => Assert.Equal("tenant-a", record.TenantId));
        Assert.Equal("value-b", (await store.GetAsync(Collection, "tenant-b", "agent-a", Author, ct))?.Value);

        Assert.True(await store.DeleteAsync(Collection, "tenant-a", "agent-a", Author, ct));
        Assert.False(await store.DeleteAsync(Collection, "tenant-a", "agent-a", Author, ct));
        Assert.Null(await store.GetAsync(Collection, "tenant-a", "agent-a", Author, ct));
        Assert.NotNull(await store.GetAsync(Collection, "tenant-b", "agent-a", Author, ct));
    }

    [Fact]
    public async Task Store_UsesAdditiveSchemaWithEfCreatedKeyVaultDatabase()
    {
        using var database = new TemporaryKeyVaultDatabase();
        await using (var db = database.CreateDbContext())
        {
            await KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db, TestContext.Current.CancellationToken);
            Assert.True(await db.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        }

        IKeyVaultRecordStore store = KeyVaultRecordStoreFactory.CreateWorkspaceStore(
            database.DatabasePath,
            database.DirectoryPath);
        await store.UpsertAsync(
            Collection,
            "tenant-a",
            "agent-hash",
            "record-value",
            Author,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "record-value",
            (await store.GetAsync(
                Collection,
                "tenant-a",
                "agent-hash",
                Author,
            TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public async Task StoreInitializedDatabase_RemainsCompatibleWithEfKeyVaultService()
    {
        using var database = new TemporaryKeyVaultDatabase();
        IKeyVaultRecordStore store = new KeyVaultRecordStore(database.DatabasePath);
        await store.UpsertAsync(
            Collection,
            "tenant-a",
            "agent-hash",
            "record-value",
            Author,
            TestContext.Current.CancellationToken);

        await using var db = database.CreateDbContext();
        await KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db, TestContext.Current.CancellationToken);
        var service = new KeyVaultService(db);
        var tenant = await service.CreateTenantAsync(
            "service-tenant",
            "test",
            TestContext.Current.CancellationToken);
        await service.SetSecretAsync(
            "service-secret",
            "service-value",
            tenant.Id,
            "test",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "service-value",
            (await service.GetSecretAsync(
                "service-secret",
                tenant.Id,
                "test",
                TestContext.Current.CancellationToken))?.Value);
    }

    [Fact]
    public async Task Store_AtomicallyUpsertsOneRecordAcrossConcurrentInstances()
    {
        using var database = new TemporaryKeyVaultDatabase();
        var ct = TestContext.Current.CancellationToken;
        var stores = Enumerable.Range(0, 8)
            .Select(_ => new KeyVaultRecordStore(database.DatabasePath))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => store.UpsertAsync(
            Collection,
            "tenant-a",
            "shared-agent",
            $"value-{index}",
            Author,
            ct)));

        var records = await stores[0].ListAsync(Collection, "tenant-a", Author, ct);
        Assert.Single(records);
        Assert.StartsWith("value-", records[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_WrapsCorruptedEncryptedValuesInKeyVaultAccessException()
    {
        using var database = new TemporaryKeyVaultDatabase();
        IKeyVaultRecordStore store = new KeyVaultRecordStore(database.DatabasePath);
        var ct = TestContext.Current.CancellationToken;
        await store.UpsertAsync(Collection, "tenant-a", "agent-hash", "valid", Author, ct);

        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE KeyVaultRecords SET EncryptedValue = 'not-base64';";
            await command.ExecuteNonQueryAsync(ct);
        }

        var exception = await Assert.ThrowsAsync<KeyVaultAccessException>(() =>
            store.GetAsync(Collection, "tenant-a", "agent-hash", Author, ct));
        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public async Task Pages_PreserveReplayBytesAndUnicodeAcrossRestartAndRejectChangedVersion()
    {
        using var db = new TemporaryKeyVaultDatabase();
        var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        var text = new string('a', 32767) + "😀" + new string('z', 100000);
        await store.UpsertAsync(Collection, "tenant-a", "session:request", text, Author, ct);
        var metadata = await store.GetMetadataAsync(Collection, "tenant-a", "session:request", Author, ct);
        Assert.NotNull(metadata?.Version);
        var first = await store.ReadRangeAsync(Collection, "tenant-a", "session:request", Author, metadata.Version, 0, 32768, ct);
        Assert.NotNull(first); Assert.Equal(32767, first.NextOffset);
        store = new(db.DatabasePath);
        var second = await store.ReadRangeAsync(Collection, "tenant-a", "session:request", Author, first.Version, first.NextOffset, 100, ct);
        Assert.StartsWith("😀", second!.Text);
        Assert.Equal(text, (await store.GetAsync(Collection, "tenant-a", "session:request", Author, ct))!.Value);
        Assert.Null(await store.ReadRangeAsync(Collection, "tenant-b", "session:request", Author, null, 0, 100, ct));
        await store.UpsertAsync(Collection, "tenant-a", "session:request", "replacement", Author, ct);
        await Assert.ThrowsAsync<KeyVaultRecordVersionException>(() => store.ReadRangeAsync(Collection, "tenant-a", "session:request", Author, first.Version, 0, 100, ct));
    }

    [Fact]
    public async Task Pages_ConvertHistoricalContentOnceWithoutChangingLogicalTimestamps()
    {
        using var db = new TemporaryKeyVaultDatabase(); var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        var before = await store.UpsertAsync(Collection, "tenant-a", "old", "original 😀 payload", Author, ct);
        for (var i = 0; i < 3; i++) Assert.NotNull(await store.ReadRangeAsync(Collection, "tenant-a", "old", Author, null, 0, 100, ct));
        var after = await store.GetAsync(Collection, "tenant-a", "old", Author, ct);
        Assert.Equal(before, after);
        await using var connection = db.CreateConnection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM KeyVaultRecordAuditEntries WHERE Operation='PrepareRecordPages';";
        Assert.Equal(1L, await command.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Metadata_IsBoundedIncrementalAndDoesNotDecryptUnrelatedPayloads()
    {
        using var db = new TemporaryKeyVaultDatabase(); var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        await store.UpsertAsync(Collection, "tenant-a", "session:a", "first", Author, ct);
        await store.UpsertAsync(Collection, "tenant-a", "other:bad", "secret", Author, ct);
        await using var connection = db.CreateConnection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE KeyVaultRecords SET EncryptedValue='corrupt' WHERE RecordKey='other:bad';";
        await command.ExecuteNonQueryAsync(ct);
        var page = await store.QueryMetadataAsync(Collection, "tenant-a", "session:", Author, count: 1, ct: ct);
        Assert.Single(page.Records); Assert.False(page.HasMore);
        await store.UpsertAsync(Collection, "tenant-a", "session:a", "updated", Author, ct);
        var delta = await store.QueryMetadataAsync(Collection, "tenant-a", "session:", Author, page.Next, ct: ct);
        Assert.Single(delta.Records); Assert.Equal("session:a", delta.Records[0].Key);
        Assert.Empty((await store.QueryMetadataAsync(Collection, "tenant-b", "session:", Author, ct: ct)).Records);
    }

    [Fact]
    public async Task Pages_AuthenticateChunksAndDoNotHydrateUnrequestedChunks()
    {
        using var db = new TemporaryKeyVaultDatabase(); var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        await store.UpsertAsync(Collection, "tenant-a", "large", new string('x', 120000), Author, ct);
        await using var connection = db.CreateConnection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE KeyVaultRecordChunks SET EncryptedValue='corrupt' WHERE Part=2;";
        await command.ExecuteNonQueryAsync(ct);
        Assert.Equal(new string('x', 100), (await store.ReadRangeAsync(Collection, "tenant-a", "large", Author, null, 0, 100, ct))!.Text);
        await Assert.ThrowsAsync<FormatException>(() => store.ReadRangeAsync(Collection, "tenant-a", "large", Author, null, 65536, 100, ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync(Collection, "tenant-a", "large", "changed", Author, cancelled.Token));
        Assert.Equal(120000, (await store.GetMetadataAsync(Collection, "tenant-a", "large", Author, ct))!.Characters);
    }

    [Fact]
    public async Task Pages_InterruptedPublicationRollsBackAndRecordCoordinatesAreAuthenticated()
    {
        using var db = new TemporaryKeyVaultDatabase(); var ct = TestContext.Current.CancellationToken;
        var store = new KeyVaultRecordStore(db.DatabasePath);
        var original = new string('a', 80000);
        await store.UpsertAsync(Collection, "tenant-a", "large", original, Author, ct);
        await using var connection = db.CreateConnection(); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER interrupt_chunks BEFORE INSERT ON KeyVaultRecordChunks WHEN NEW.Part=1 BEGIN SELECT RAISE(ABORT,'interrupted fixture'); END;";
        await command.ExecuteNonQueryAsync(ct);
        await Assert.ThrowsAsync<KeyVaultAccessException>(() => store.UpsertAsync(Collection, "tenant-a", "large", new string('b', 100000), Author, ct));
        Assert.Equal(original, (await new KeyVaultRecordStore(db.DatabasePath).GetAsync(Collection, "tenant-a", "large", Author, ct))!.Value);
        command.CommandText = "DROP TRIGGER interrupt_chunks; UPDATE KeyVaultRecords SET TenantId='tenant-b' WHERE RecordKey='large';";
        await command.ExecuteNonQueryAsync(ct);
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => store.ReadRangeAsync(Collection, "tenant-b", "large", Author, null, 0, 100, ct));
    }

    private sealed class TemporaryKeyVaultDatabase : IDisposable
    {
        public TemporaryKeyVaultDatabase()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "gnougo-keyvault-record-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "gnougo-keyvault.db");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }

        public KeyVaultDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<KeyVaultDbContext>()
                .UseSqlite($"Data Source={DatabasePath}")
                .Options);

        public SqliteConnection CreateConnection() => new($"Data Source={DatabasePath}");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
