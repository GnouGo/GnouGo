using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.DocsIngestor.Mcp.Tests;

public sealed class DocsIngestorMcpWebHostTests
{
    [Fact]
    public async Task Build_StartsHttpHost_AndExposesHealthEndpoint()
    {
        var root = CreateTempDir();
        var app = DocsIngestorMcpWebHost.Build([
            $"--DocsIngestorMcp:DatabasePath={Path.Combine(root, "metadata.db")}",
            $"--DocsIngestorMcp:VectorDatabasePath={Path.Combine(root, "vectors.sqlite")}",
            $"--DocsIngestorMcp:OriginalsDirectory={Path.Combine(root, "originals")}",
            $"--KeyVault:DatabasePath={Path.Combine(root, "keyvault.db")}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();

            using var http = new HttpClient();
            var payload = await http.GetFromJsonAsync<HealthPayload>($"{address.TrimEnd('/')}/health");

            Assert.NotNull(payload);
            Assert.Equal("ok", payload.Status);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            TryDelete(root);
        }
    }

    private sealed record HealthPayload(string Status);

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gnougo-docs-ingestor-mcp-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite WAL files can be released slightly after provider disposal on Windows.
        }
    }
}


