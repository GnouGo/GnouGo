using Microsoft.EntityFrameworkCore;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class KeyVaultServiceTests : IAsyncDisposable
{
    private readonly KeyVaultDbContext _db;
    private readonly KeyVaultService _svc;

    public KeyVaultServiceTests()
    {
        var options = new DbContextOptionsBuilder<KeyVaultDbContext>()
            .UseSqlite($"Data Source={Path.GetTempFileName()}")
            .Options;

        _db = new KeyVaultDbContext(options);
        _db.Database.EnsureCreated();
        _svc = new KeyVaultService(_db);
        _svc.EnsureDefaultKeyPairAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task CreateTenant_ReturnsDto()
    {
        var tenant = await _svc.CreateTenantAsync("TestCorp", "admin");

        Assert.Equal("TestCorp", tenant.Name);
        Assert.Equal("admin", tenant.CreatedBy);
        Assert.NotEqual(Guid.Empty, tenant.Id);
    }

    [Fact]
    public async Task ListTenants_DoesNotIncludeDeleted()
    {
        var t = await _svc.CreateTenantAsync("ToDelete", "admin");
        await _svc.DeleteTenantAsync(t.Id, "admin");

        var tenants = await _svc.ListTenantsAsync();
        Assert.DoesNotContain(tenants, x => x.Id == t.Id);
    }

    [Fact]
    public async Task ListTenants_ExcludesDefaultSentinel()
    {
        var tenants = await _svc.ListTenantsAsync();
        Assert.DoesNotContain(tenants, x => x.Name == "__default__");
    }

    [Fact]
    public async Task SetAndGetSecret_DefaultTenant_RoundTrip()
    {
        await _svc.SetSecretAsync("DB_PASSWORD", "s3cur3!", null, "dev");
        var result = await _svc.GetSecretAsync("DB_PASSWORD", null, "dev");

        Assert.NotNull(result);
        Assert.Equal("s3cur3!", result!.Value);
        Assert.Equal(1, result.Version);
    }

    [Fact]
    public async Task SetSecret_MultipleVersions_IncrementsVersion()
    {
        await _svc.SetSecretAsync("API_KEY", "v1-value", null, "dev");
        await _svc.SetSecretAsync("API_KEY", "v2-value", null, "dev");
        await _svc.SetSecretAsync("API_KEY", "v3-value", null, "dev");

        var result = await _svc.GetSecretAsync("API_KEY", null, "reader");

        Assert.NotNull(result);
        Assert.Equal("v3-value", result!.Value);
        Assert.Equal(3, result.Version);
    }

    [Fact]
    public async Task GetSecretVersions_ReturnsAllVersions()
    {
        await _svc.SetSecretAsync("VERSIONED", "a", null, "alice");
        await _svc.SetSecretAsync("VERSIONED", "b", null, "bob");

        var versions = await _svc.GetSecretVersionsAsync("VERSIONED", null);

        Assert.Equal(2, versions.Count);
        Assert.Equal(2, versions[0].Version);
        Assert.Equal(1, versions[1].Version);
    }

    [Fact]
    public async Task Secrets_IsolatedPerTenant()
    {
        var tenantA = await _svc.CreateTenantAsync("TenantA", "admin");
        var tenantB = await _svc.CreateTenantAsync("TenantB", "admin");

        await _svc.SetSecretAsync("SHARED_KEY", "value-A", tenantA.Id, "admin");
        await _svc.SetSecretAsync("SHARED_KEY", "value-B", tenantB.Id, "admin");

        var valA = await _svc.GetSecretAsync("SHARED_KEY", tenantA.Id, "reader");
        var valB = await _svc.GetSecretAsync("SHARED_KEY", tenantB.Id, "reader");

        Assert.NotNull(valA);
        Assert.NotNull(valB);
        Assert.Equal("value-A", valA!.Value);
        Assert.Equal("value-B", valB!.Value);
    }

    [Fact]
    public async Task GetSecret_WrongTenant_ReturnsNull()
    {
        await _svc.SetSecretAsync("ONLY_DEFAULT", "mine", null, "dev");

        var tenantA = await _svc.CreateTenantAsync("Alien", "admin");
        var result = await _svc.GetSecretAsync("ONLY_DEFAULT", tenantA.Id, "dev");

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteSecret_SoftDeletes()
    {
        await _svc.SetSecretAsync("TO_DELETE", "gone", null, "dev");
        var deleted = await _svc.DeleteSecretAsync("TO_DELETE", null, "dev");
        Assert.True(deleted);

        var result = await _svc.GetSecretAsync("TO_DELETE", null, "dev");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteSecret_NonExistent_ReturnsFalse()
    {
        var result = await _svc.DeleteSecretAsync("NOPE", null, "dev");
        Assert.False(result);
    }

    [Fact]
    public async Task ListSecrets_ReturnsKeys()
    {
        await _svc.SetSecretAsync("KEY_A", "a", null, "dev");
        await _svc.SetSecretAsync("KEY_B", "b", null, "dev");

        var list = await _svc.ListSecretsAsync(null);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Key == "KEY_A");
        Assert.Contains(list, s => s.Key == "KEY_B");
    }

    [Fact]
    public async Task ListSecrets_ExcludesDeleted()
    {
        await _svc.SetSecretAsync("LIVE", "yes", null, "dev");
        await _svc.SetSecretAsync("DEAD", "no", null, "dev");
        await _svc.DeleteSecretAsync("DEAD", null, "dev");

        var list = await _svc.ListSecretsAsync(null);
        Assert.Single(list);
        Assert.Equal("LIVE", list[0].Key);
    }

    [Fact]
    public async Task SetSecret_CreatesAuditEntry()
    {
        await _svc.SetSecretAsync("AUDITED", "val", null, "auditor");

        var log = await _svc.GetAuditLogAsync(null, "AUDITED", 0, 10);
        Assert.NotEmpty(log);
        Assert.Contains(log, e => e.Operation == "SetSecret" && e.Author == "auditor");
    }

    [Fact]
    public async Task GetSecret_CreatesAuditEntry()
    {
        await _svc.SetSecretAsync("READ_ME", "val", null, "writer");
        await _svc.GetSecretAsync("READ_ME", null, "reader");

        var log = await _svc.GetAuditLogAsync(null, "READ_ME", 0, 10);
        Assert.Contains(log, e => e.Operation == "GetSecret" && e.Author == "reader");
    }

    [Fact]
    public async Task CreateTenant_CreatesAuditEntry()
    {
        await _svc.CreateTenantAsync("AuditedTenant", "admin");

        var log = await _svc.GetAuditLogAsync(null, null, 0, 50);
        Assert.Contains(log, e => e.Operation == "CreateTenant" && e.Author == "admin");
    }

    [Fact]
    public async Task SetAndGetSecret_LargeValue_Works()
    {
        var longValue = new string('X', 10_000);
        await _svc.SetSecretAsync("BIG_SECRET", longValue, null, "dev");

        var result = await _svc.GetSecretAsync("BIG_SECRET", null, "dev");

        Assert.NotNull(result);
        Assert.Equal(longValue, result!.Value);
    }
}


