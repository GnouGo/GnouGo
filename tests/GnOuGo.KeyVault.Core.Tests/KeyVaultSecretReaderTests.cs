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
            "LLM--Models--OpenAi",
            "provider-secret-value",
            tenantId: null,
            author: "GnOuGo.Agent.Server",
            ct: TestContext.Current.CancellationToken);

        IKeyVaultSecretReader reader = KeyVaultSecretReaderFactory.CreateWorkspaceReader(
            database.DatabasePath,
            database.DirectoryPath);
        var result = await reader.GetFirstDefaultTenantSecretValueAsync(
            ["missing-key", "LLM--Models--OpenAi"],
            "GnOuGo.GithubCopilot.Mcp",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("LLM--Models--OpenAi", result.Key);
        Assert.Equal("provider-secret-value", result.Value);

        db.ChangeTracker.Clear();
        var storedVersion = await db.SecretVersions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual("provider-secret-value", storedVersion.EncryptedValue);
        Assert.DoesNotContain("provider-secret-value", storedVersion.EncryptedValue, StringComparison.Ordinal);

        var readAudit = await db.AuditEntries.SingleAsync(
            entry => entry.Operation == AuditOperation.GetSecret
                     && entry.SecretKey == "LLM--Models--OpenAi"
                     && entry.Author == "GnOuGo.GithubCopilot.Mcp",
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
                "LLM--Models--OpenAi",
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
            "LLM--Models--OpenAi",
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
                "LLM--Models--OpenAi",
                "test",
                TestContext.Current.CancellationToken));

        Assert.IsType<FormatException>(exception.InnerException);
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
