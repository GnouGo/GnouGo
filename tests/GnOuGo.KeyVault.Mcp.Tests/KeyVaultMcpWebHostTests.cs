using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.KeyVault.Mcp.Tests;

public sealed class KeyVaultMcpWebHostTests
{
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


