using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.KeyVault.Mcp.Tests;

public sealed class KeyVaultToolsTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly KeyVaultTools _tools;

    public KeyVaultToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tools-{Guid.NewGuid():N}.db");
        var store = new KeyVaultSqliteStore(_dbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        _tools = new KeyVaultTools(store, NullLogger<KeyVaultTools>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-shm");
        TryDelete(_dbPath + "-wal");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SetSecretAndGetSecret_RoundTrip_ReturnsMetadataAndValue()
    {
        var tenant = await _tools.CreateTenantAsync("tools-test-tenant", "tester");
        var tenantId = tenant.Data!.Id;

        var set = await _tools.SetSecretAsync("demo", "secret-value", "tester", tenantId);
        Assert.True(set.Success);

        var get = await _tools.GetSecretAsync("demo", "tester", tenantId);
        Assert.True(get.Success);
        Assert.NotNull(get.Data);

        Assert.Equal("secret-value", get.Data.Value);
    }

    [Fact]
    public async Task GetSecret_WhenMissing_ReturnsNotFound()
    {
        var result = await _tools.GetSecretAsync("missing", "tester");

        Assert.False(result.Success);
        Assert.Equal("Secret 'missing' not found.", result.Error);
    }

    [Fact]
    public async Task GetSecret_ReturnsLatestVersionValue()
    {
        var tenant = await _tools.CreateTenantAsync("versioned-test-tenant", "tester");
        var tenantId = tenant.Data!.Id;

        await _tools.SetSecretAsync("versioned", "v1", "tester", tenantId);
        await _tools.SetSecretAsync("versioned", "v2", "tester", tenantId);

        var result = await _tools.GetSecretAsync("versioned", "tester", tenantId);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        Assert.Equal("v2", result.Data.Value);
        Assert.Equal(2, result.Data.Version);
    }

    [Fact]
    public async Task ListSecretsAndDeleteSecret_ReturnsMetadataThenNotFound()
    {
        await _tools.SetSecretAsync("default-demo", "value", "tester");

        var list = await _tools.ListSecretsAsync();

        Assert.True(list.Success);
        var secret = Assert.Single(list.Data!);
        Assert.Equal("default-demo", secret.Key);
        Assert.Equal(1, secret.LatestVersion);

        var delete = await _tools.DeleteSecretAsync("default-demo", "tester");
        Assert.True(delete.Success);
        Assert.Equal("Secret deleted.", delete.Data!.Message);

        var get = await _tools.GetSecretAsync("default-demo", "tester");
        Assert.False(get.Success);
        Assert.Equal("Secret 'default-demo' not found.", get.Error);
    }

    [Fact]
    public void KeyVaultTools_DoesNotExposeRemovedManagementOrLegacyMethods()
    {
        Assert.Null(typeof(KeyVaultTools).GetMethod("DeleteTenantAsync"));
        Assert.Null(typeof(KeyVaultTools).GetMethod("GetAuditLogAsync"));
        Assert.Null(typeof(KeyVaultTools).GetMethod("GetSecretVersionsAsync"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup for a temporary SQLite file.
        }
    }
}

