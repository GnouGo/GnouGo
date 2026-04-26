using GnOuGo.DocIngestor.Mcp.Data;
using GnOuGo.DocIngestor.Mcp.Models;

namespace GnOuGo.DocIngestor.Mcp.Tests;

public sealed class StoredDocumentRepositoryTests
{
    [Fact]
    public async Task UpsertListGetDelete_RoundTripsMetadata()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "metadata.db");
        var repository = new StoredDocumentRepository(dbPath);
        await repository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var record = new StoredDocumentRecord(
            "doc.txt:abcdef123456",
            "tenant-a",
            "http://127.0.0.1/doc.txt",
            "doc.txt",
            "text/plain",
            42,
            "abcdef",
            "collection-a",
            "hash-384",
            Path.Combine(root, "doc.txt"),
            3,
            now,
            now);

        await repository.UpsertAsync(record);

        var byId = await repository.GetByIdAsync(record.Id);
        Assert.NotNull(byId);
        Assert.Equal(record.Sha256, byId.Sha256);
        Assert.Equal(TimeSpan.Zero, byId.CreatedUtc.Offset);

        var bySource = await repository.GetBySourceAsync(record.TenantId, record.Collection, record.SourceUrl);
        Assert.NotNull(bySource);
        Assert.Equal(record.Id, bySource.Id);

        var listed = await repository.ListAsync("tenant-a", "collection-a");
        Assert.Single(listed);

        Assert.True(await repository.DeleteAsync(record.Id));
        Assert.Empty(await repository.ListAsync("tenant-a", "collection-a"));

        TryDelete(root);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gnougo-docs-ingestor-mcp-tests", Guid.NewGuid().ToString("N"));
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
            // SQLite WAL files can be released slightly after connection disposal on Windows.
        }
    }
}


