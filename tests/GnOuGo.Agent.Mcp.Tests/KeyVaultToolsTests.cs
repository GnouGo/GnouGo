using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class KeyVaultToolsTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly KeyVaultTools _tools;

    public KeyVaultToolsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<KeyVaultService>();
        services.AddSingleton<IServiceScopeFactory>(sp => sp.GetRequiredService<IServiceProvider>().GetRequiredService<IServiceScopeFactory>());

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
        db.Database.EnsureCreated();
        var service = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        service.EnsureDefaultKeyPairAsync().GetAwaiter().GetResult();

        _tools = new KeyVaultTools(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<KeyVaultTools>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SetSecretAndGetSecret_RoundTrip_ReturnsMetadataAndValue()
    {
        var tenant = await _tools.CreateTenantAsync("tools-test-tenant", "tester");
        var tenantId = (Guid)tenant.Data!.GetType().GetProperty("Id")!.GetValue(tenant.Data)!;

        var set = await _tools.SetSecretAsync("demo", "secret-value", "tester", tenantId);
        Assert.True(set.Success);

        var get = await _tools.GetSecretAsync("demo", "tester", tenantId);
        Assert.True(get.Success);
        Assert.NotNull(get.Data);

        var payload = get.Data!.GetType().GetProperty("Value")!.GetValue(get.Data)?.ToString();
        Assert.Equal("secret-value", payload);
    }

    [Fact]
    public async Task GetSecret_WhenMissing_ReturnsNotFound()
    {
        var result = await _tools.GetSecretAsync("missing", "tester");

        Assert.False(result.Success);
        Assert.Equal("Secret 'missing' not found.", result.Error);
    }
}



