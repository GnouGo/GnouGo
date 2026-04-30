using GnOuGo.DocIngestor.Mcp.Models;
using GnOuGo.DocIngestor.Mcp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.DocIngestor.Mcp.Tests;

public sealed class DocsIngestorMcpServiceTests
{
    [Fact]
    public async Task IngestSearchDownloadDelete_IsIdempotentByHash()
    {
        var root = CreateTempDir();
        var fileServer = BuildFileServer("alpha beta gamma alpha beta gamma");
        var mcp = DocsIngestorMcpWebHost.Build([
            $"--DocsIngestorMcp:DatabasePath={Path.Combine(root, "metadata.db")}",
            $"--DocsIngestorMcp:VectorDatabasePath={Path.Combine(root, "vectors.sqlite")}",
            $"--DocsIngestorMcp:OriginalsDirectory={Path.Combine(root, "originals")}",
            $"--DocsIngestorMcp:Chunking:TargetTokens=20",
            $"--KeyVault:DatabasePath={Path.Combine(root, "keyvault.db")}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await fileServer.StartAsync();
            await mcp.StartAsync();

            var fileAddress = fileServer.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            var fileUrl = $"{fileAddress.TrimEnd('/')}/docs/sample.txt";

            await using var scope = mcp.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<DocsIngestorMcpService>();

            var vectorized = await service.VectorizeAsync(CreateVectorizeRequest(fileUrl));
            Assert.Single(vectorized);
            Assert.NotEmpty(vectorized[0].Chunks);
            Assert.Equal(vectorized[0].Chunks.OrderBy(c => c.Index).Select(c => c.ChunkId), vectorized[0].Chunks.Select(c => c.ChunkId));

            var ingestRequest = CreateIngestRequest(fileUrl);
            var first = await service.IngestAsync(ingestRequest, keyVaultTenantId: null);
            Assert.Single(first);
            Assert.False(first[0].Skipped);
            Assert.Equal("created", first[0].Action);

            var second = await service.IngestAsync(ingestRequest, keyVaultTenantId: null);
            Assert.Single(second);
            Assert.True(second[0].Skipped);
            Assert.Equal("unchanged", second[0].Action);

            var listed = await service.ListFilesAsync("tenant-a", "collection-a");
            var stored = Assert.Single(listed);
            Assert.Equal(first[0].DocumentId, stored.Id);

            var hits = await service.SearchAsync("alpha beta", "collection-a", "hash-384", null, "tester", 5);
            Assert.NotEmpty(hits);

            var original = await service.DownloadOriginalAsync(stored.Id);
            Assert.NotNull(original);
            Assert.Equal("sample.txt", original.FileName);
            Assert.Contains("alpha beta", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(original.Base64Content)));

            Assert.True(await service.DeleteFileAsync(stored.Id));
            Assert.Empty(await service.ListFilesAsync("tenant-a", "collection-a"));
        }
        finally
        {
            await mcp.StopAsync();
            await mcp.DisposeAsync();
            await fileServer.StopAsync();
            await fileServer.DisposeAsync();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task IngestAndSearch_RejectDifferentEmbeddingConfigForExistingCollection()
    {
        var root = CreateTempDir();
        var fileServer = BuildFileServer("alpha beta gamma alpha beta gamma");
        var mcp = DocsIngestorMcpWebHost.Build([
            $"--DocsIngestorMcp:DatabasePath={Path.Combine(root, "metadata.db")}",
            $"--DocsIngestorMcp:VectorDatabasePath={Path.Combine(root, "vectors.sqlite")}",
            $"--DocsIngestorMcp:OriginalsDirectory={Path.Combine(root, "originals")}",
            $"--DocsIngestorMcp:Chunking:TargetTokens=20",
            $"--KeyVault:DatabasePath={Path.Combine(root, "keyvault.db")}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await fileServer.StartAsync();
            await mcp.StartAsync();

            var fileAddress = fileServer.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            var fileUrl = $"{fileAddress.TrimEnd('/')}/docs/sample.txt";

            await using var scope = mcp.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<DocsIngestorMcpService>();

            await service.IngestAsync(CreateIngestRequest(fileUrl, "hash-384"), keyVaultTenantId: null);

            var ingestError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.IngestAsync(CreateIngestRequest(fileUrl, "hash-768"), keyVaultTenantId: null));
            Assert.Contains("already uses embedding config 'hash-384'", ingestError.Message);
            Assert.Contains("embeddingConfigName='hash-384'", ingestError.Message);

            var searchError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SearchAsync("alpha", "collection-a", "hash-768", null, "tester", 5));
            Assert.Contains("already uses embedding config 'hash-384'", searchError.Message);
            Assert.Contains("embeddingConfigName='hash-384'", searchError.Message);
        }
        finally
        {
            await mcp.StopAsync();
            await mcp.DisposeAsync();
            await fileServer.StopAsync();
            await fileServer.DisposeAsync();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Ingest_WithoutEmbeddingConfigOrDefault_ReturnsMandatoryEmbeddingError()
    {
        var root = CreateTempDir();
        var mcp = DocsIngestorMcpWebHost.Build([
            $"--DocsIngestorMcp:DatabasePath={Path.Combine(root, "metadata.db")}",
            $"--DocsIngestorMcp:VectorDatabasePath={Path.Combine(root, "vectors.sqlite")}",
            $"--DocsIngestorMcp:OriginalsDirectory={Path.Combine(root, "originals")}",
            $"--KeyVault:DatabasePath={Path.Combine(root, "keyvault.db")}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await mcp.StartAsync();
            await using var scope = mcp.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<DocsIngestorMcpService>();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.IngestAsync(CreateIngestRequest("http://127.0.0.1/docs/sample.txt", string.Empty), keyVaultTenantId: null));

            Assert.Contains("Embedding configuration is required", error.Message);
            Assert.Contains("/embedding add", error.Message);
        }
        finally
        {
            await mcp.StopAsync();
            await mcp.DisposeAsync();
            TryDelete(root);
        }
    }

    private static WebApplication BuildFileServer(string content)
    {
        var builder = WebApplication.CreateSlimBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/docs/sample.txt", () => Results.Text(content, "text/plain"));
        return app;
    }

    private static FileVectorizationRequest CreateVectorizeRequest(string fileUrl) => new(
        [fileUrl],
        "tenant-a",
        "recursive",
        1,
        20,
        40,
        0,
        false,
        false,
        false,
        "eng",
        300);

    private static FileIngestionRequest CreateIngestRequest(string fileUrl, string embeddingConfigName = "hash-384") => new(
        [fileUrl],
        "tenant-a",
        "collection-a",
        embeddingConfigName,
        "recursive",
        1,
        20,
        40,
        0,
        false,
        false,
        false,
        "eng",
        300,
        "tester");

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gnougo-docs-ingestor-mcp-service-tests", Guid.NewGuid().ToString("N"));
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
            // SQLite WAL files can be released slightly after provider disposal on some systems.
        }
    }
}


