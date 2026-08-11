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
