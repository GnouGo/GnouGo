using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Models;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.KeyVault.Core.Tests;

public sealed class KeyVaultSecretReaderTests
{
    [Fact]
    public async Task Reader_LoadsServiceWrittenSecretAndRecordsAuditWithoutPlaintextAtRest()
    {
        using var database = new TemporaryKeyVaultDatabase();
        await using var db = database.CreateDbContext();
        await KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db, TestContext.Current.CancellationToken);
        var service = new KeyVaultService(db);
        await service.EnsureDefaultKeyPairAsync(TestContext.Current.CancellationToken);
        await service.SetSecretAsync(
            "application--config--primary",
            "provider-secret-value",
            tenantId: null,
            author: "generic-writer",
            ct: TestContext.Current.CancellationToken);

        IKeyVaultSecretReader reader = KeyVaultSecretReaderFactory.CreateWorkspaceReader(
            database.DatabasePath,
            database.DirectoryPath);
        var result = await reader.GetFirstDefaultTenantSecretValueAsync(
            ["missing-key", "application--config--primary"],
            "generic-reader",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("application--config--primary", result.Key);
        Assert.Equal("provider-secret-value", result.Value);

        db.ChangeTracker.Clear();
        var storedVersion = await db.SecretVersions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("provider-secret-value", storedVersion.EncryptedValue);
        Assert.DoesNotContain("provider-secret-value", storedVersion.EncryptedValue, StringComparison.Ordinal);

        var readAudit = await db.AuditEntries.SingleAsync(
            entry => entry.Operation == AuditOperation.GetSecret
                     && entry.SecretKey == "application--config--primary"
                     && entry.Author == "generic-reader",
            TestContext.Current.CancellationToken);
        Assert.Equal("Read version 1", readAudit.Details);
    }

    [Fact]
    public async Task Reader_WrapsStorageFailuresInKeyVaultAccessException()
    {
        using var database = new TemporaryKeyVaultDatabase();
        await File.WriteAllTextAsync(
            database.DatabasePath,
            "not a SQLite database",
            TestContext.Current.CancellationToken);
        IKeyVaultSecretReader reader = new KeyVaultSecretReader(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<KeyVaultAccessException>(() =>
            reader.GetDefaultTenantSecretValueAsync(
                "application--config--primary",
                "test",
                TestContext.Current.CancellationToken));

        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task Reader_WrapsDecryptionFailuresInKeyVaultAccessException()
    {
        using var database = new TemporaryKeyVaultDatabase();
        await using var db = database.CreateDbContext();
        await KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db, TestContext.Current.CancellationToken);
        var service = new KeyVaultService(db);
        await service.EnsureDefaultKeyPairAsync(TestContext.Current.CancellationToken);
        await service.SetSecretAsync(
            "application--config--primary",
            "provider-secret-value",
            tenantId: null,
            author: "test",
            ct: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE SecretVersions SET EncryptedValue = 'not-base64';",
            TestContext.Current.CancellationToken);
        IKeyVaultSecretReader reader = new KeyVaultSecretReader(database.DatabasePath);

        var exception = await Assert.ThrowsAsync<KeyVaultAccessException>(() =>
            reader.GetDefaultTenantSecretValueAsync(
                "application--config--primary",
                "test",
                TestContext.Current.CancellationToken));

        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public async Task CatalogReader_LoadsLatestDefaultTenantValuesByLiteralCaseInsensitivePrefix()
    {
        using var database = new TemporaryKeyVaultDatabase();
        await using var db = database.CreateDbContext();
        var ct = TestContext.Current.CancellationToken;
        await KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db, ct);
        var service = new KeyVaultService(db);
        await service.EnsureDefaultKeyPairAsync(ct);
        await service.SetSecretAsync("application--config--Alpha", "old", null, "writer", ct);
        await service.SetSecretAsync("application--config--Alpha", "latest", null, "writer", ct);
        await service.SetSecretAsync("application--config--Beta", "second", null, "writer", ct);
        await service.SetSecretAsync("application--config%wildcard", "excluded", null, "writer", ct);
        await service.SetSecretAsync("application--other--Gamma", "excluded", null, "writer", ct);
        var tenant = await service.CreateTenantAsync("isolated", "writer", ct);
        await service.SetSecretAsync("application--config--TenantOnly", "excluded", tenant.Id, "writer", ct);

        IKeyVaultSecretCatalogReader reader = KeyVaultSecretReaderFactory.CreateWorkspaceCatalogReader(
            database.DatabasePath,
            database.DirectoryPath);
        var values = await reader.GetDefaultTenantSecretValuesByPrefixAsync(
            "APPLICATION--CONFIG--",
            "generic-consumer",
            ct);

        Assert.Equal(2, values.Count);
        Assert.Equal("latest", Assert.Single(values, value => value.Key.EndsWith("Alpha", StringComparison.Ordinal)).Value);
        Assert.Equal("second", Assert.Single(values, value => value.Key.EndsWith("Beta", StringComparison.Ordinal)).Value);

        db.ChangeTracker.Clear();
        var audits = await db.AuditEntries
            .Where(entry => entry.Operation == AuditOperation.GetSecret
                            && entry.Author == "generic-consumer")
            .ToListAsync(ct);
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, entry => entry.SecretKey == "application--config--Alpha" && entry.Details == "Read version 2");
    }

    [Fact]
    public async Task CatalogReader_ReturnsEmptyWhenDatabaseDoesNotExist()
    {
        using var database = new TemporaryKeyVaultDatabase();
        IKeyVaultSecretCatalogReader reader = new KeyVaultSecretReader(database.DatabasePath);

        var values = await reader.GetDefaultTenantSecretValuesByPrefixAsync(
            "application--",
            "test",
            TestContext.Current.CancellationToken);

        Assert.Empty(values);
    }

    [Fact]
    public async Task CatalogReader_WrapsStorageFailuresInKeyVaultAccessException()
    {
        using var database = new TemporaryKeyVaultDatabase();
        await File.WriteAllTextAsync(
            database.DatabasePath,
            "not a SQLite database",
            TestContext.Current.CancellationToken);
        IKeyVaultSecretCatalogReader reader = new KeyVaultSecretReader(database.DatabasePath);

        await Assert.ThrowsAsync<KeyVaultAccessException>(() =>
            reader.GetDefaultTenantSecretValuesByPrefixAsync(
                "application--",
                "test",
                TestContext.Current.CancellationToken));
    }

    private sealed class TemporaryKeyVaultDatabase : IDisposable
    {
        public TemporaryKeyVaultDatabase()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "gnougo-keyvault-core-tests",
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

        public void Dispose()
        {
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
