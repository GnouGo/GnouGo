using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using GnOuGo.KeyVault.Core;
using GnOuGo.Workspace;

namespace GnOuGo.KeyVault.Mcp.Tests;

public sealed class KeyVaultMcpWebHostTests
{
    [Fact]
    public void ResolveDatabasePath_WhenUsingDefaultPath_UsesDefaultWorkingDirectoryGnOuGoDataDirectory()
    {
        var expected = Path.Combine(
            GnOuGoWorkspace.ResolveDefaultWorkingDirectory(),
            ".GnOuGo",
            "data",
            "gnougo-keyvault.db");

        var actual = KeyVaultDatabasePathResolver.Resolve(
            KeyVaultDatabasePathResolver.DefaultRelativePath,
            AppContext.BaseDirectory);

        Assert.Equal(expected, actual);
    }

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

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Tenants WHERE Name = '__default__' AND IsDeleted = 0;";

            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
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
