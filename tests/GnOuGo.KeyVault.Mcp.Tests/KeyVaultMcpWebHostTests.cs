using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GnOuGo.KeyVault.Core.Data;

namespace GnOuGo.KeyVault.Mcp.Tests;

public sealed class KeyVaultMcpWebHostTests
{
    [Fact]
    public async Task InitializeKeyVaultMcpAsync_CreatesSchemaAndDefaultTenant()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-init-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddKeyVaultMcpPersistence(dbPath);

        await using var provider = services.BuildServiceProvider();

        try
        {
            await provider.InitializeKeyVaultMcpAsync();

            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
            var defaultTenant = await db.Tenants.FirstOrDefaultAsync(t => t.Name == "__default__");

            Assert.NotNull(defaultTenant);
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a temporary SQLite file.
            }
        }
    }

    [Fact]
    public async Task Build_StartsHttpHost_AndExposesHealthEndpoint()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-mcp-{Guid.NewGuid():N}.db");
        var app = KeyVaultMcpWebHost.Build([
            $"--KeyVault:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();

            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            using var http = new HttpClient();
            var payload = await http.GetFromJsonAsync<HealthPayload>($"{address.TrimEnd('/')}/health");

            Assert.NotNull(payload);
            Assert.Equal("ok", payload.Status);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a temporary SQLite file.
            }
        }
    }

    private sealed record HealthPayload(string Status);
}


