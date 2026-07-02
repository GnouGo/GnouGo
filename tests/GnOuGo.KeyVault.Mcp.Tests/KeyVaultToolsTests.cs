using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using ModelContextProtocol.Server;

namespace GnOuGo.KeyVault.Mcp.Tests;

public sealed class KeyVaultToolsTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _serviceProvider;
    private readonly KeyVaultTools _tools;

    public KeyVaultToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tools-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddDbContext<KeyVaultDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddScoped<KeyVaultService>();
        _serviceProvider = services.BuildServiceProvider();

        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
        KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db).GetAwaiter().GetResult();
        var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        svc.EnsureDefaultKeyPairAsync().GetAwaiter().GetResult();

        _tools = new KeyVaultTools(svc, NullLogger<KeyVaultTools>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-shm");
        TryDelete(_dbPath + "-wal");
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
        Assert.False(result.Ok);
        Assert.Equal("Secret 'missing' not found.", result.Error);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Equal("Secret 'missing' not found.", result.ErrorMessage);
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
        Assert.False(get.Ok);
        Assert.Equal("Secret 'default-demo' not found.", get.Error);
        Assert.Equal("NOT_FOUND", get.ErrorCode);
    }

    [Fact]
    public void KeyVaultTools_DoesNotExposeRemovedManagementOrLegacyMethods()
    {
        Assert.Null(typeof(KeyVaultTools).GetMethod("DeleteTenantAsync"));
        Assert.Null(typeof(KeyVaultTools).GetMethod("GetAuditLogAsync"));
        Assert.Null(typeof(KeyVaultTools).GetMethod("GetSecretVersionsAsync"));
    }

    [Fact]
    public void AllKeyVaultMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(KeyVaultTools)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false).Cast<McpServerToolAttribute>().SingleOrDefault()
            })
            .Where(item => item.Attribute != null)
            .ToArray();

        Assert.NotEmpty(toolMethods);
        Assert.All(toolMethods, item =>
        {
            Assert.True(item.Attribute!.UseStructuredContent, item.Method.Name);
            Assert.NotNull(item.Attribute.OutputSchemaType);
            Assert.Equal(UnwrapToolReturnType(item.Method.ReturnType), item.Attribute.OutputSchemaType);
        });
    }

    private static Type UnwrapToolReturnType(Type returnType)
        => returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

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
