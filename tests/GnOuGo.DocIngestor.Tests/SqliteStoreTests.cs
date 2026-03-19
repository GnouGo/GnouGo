using DocIngestor.Core.Models;
using DocIngestor.Core.Stores;
using Xunit;

namespace DocIngestor.Tests;

public sealed class SqliteStoreTests
{
    [Fact]
    public async Task SqliteStore_Search_Returns_Best_Match()
    {
        var tmp = CreateTempDir();
        var dbPath = Path.Combine(tmp, "vectors.sqlite");

        var store = new SqliteCosineVectorStore(dbPath);
        var collection = "col";

        var c1 = new TextChunk("c1", "d1", "s1", 0, "cats cats", new Dictionary<string, string>());
        var c2 = new TextChunk("c2", "d1", "s1", 1, "cpu cpu", new Dictionary<string, string>());

        var v1 = new float[] { 1f, 0f, 0f, 0f };
        var v2 = new float[] { 0f, 1f, 0f, 0f };

        await store.UpsertAsync(collection, new[]
        {
            new EmbeddedChunk(c1, "manual", v1),
            new EmbeddedChunk(c2, "manual", v2),
        });

        var query = new float[] { 0.9f, 0.1f, 0f, 0f };

        var results = await store.SearchAsync(collection, query, topK: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("c1", results[0].Chunk.Chunk.ChunkId);
        Assert.True(results[0].Score > results[1].Score);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DocIngestorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
