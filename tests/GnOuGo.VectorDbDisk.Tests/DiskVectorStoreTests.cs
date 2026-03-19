using GnOuGo.VectorDbDisk;
using Xunit;

namespace GnOuGo.VectorDbDisk.Tests;

public sealed class DiskVectorStoreTests
{
    [Fact]
    public async Task Metadata_Prefilter_OnDisk_And_VectorSearch_Works()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-vectordbdisk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var embedder = new HfMiniLmEmbedder(Path.Combine(root, ".hf-cache"));
        var ready = await embedder.EnsureReadyAsync(CancellationToken.None);
        if (!ready) return;

        await using var _ = embedder;

        var store = new DiskVectorStore(DiskVectorStoreOptions.Default(root));

        await store.AddManyAsync("demo", new[]
        {
            VectorDocument.Create("cat", "A cat is a small domesticated carnivorous mammal.",
                embedder.Embed("A cat is a small domesticated carnivorous mammal."),
                new Dictionary<string, string> { { "type", "animal" } }),
            VectorDocument.Create("dog", "A dog is a domesticated descendant of the wolf.",
                embedder.Embed("A dog is a domesticated descendant of the wolf."),
                new Dictionary<string, string> { { "type", "animal" } }),
            VectorDocument.Create("car", "A car is a wheeled motor vehicle used for transportation.",
                embedder.Embed("A car is a wheeled motor vehicle used for transportation."),
                new Dictionary<string, string> { { "type", "vehicle" } }),
        });

        var hits = await store.SearchAsync("demo",
            queryVector: embedder.Embed("kitten"),
            options: new SearchOptions(TopK: 2, Filter: MetadataFilter.Eq("type", "animal"),
                Mode: SearchMode.VectorOnly));

        Assert.True(hits.Count > 0);
        Assert.DoesNotContain(hits, h => h.Metadata.TryGetValue("type", out var t) && t == "vehicle");
        Assert.Equal("cat", hits[0].Id);
    }

    [Fact]
    public async Task Search_Works_After_New_Instance_Like_Reload()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-vectordbdisk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var embedder = new HfMiniLmEmbedder(Path.Combine(root, ".hf-cache"));
        var ready = await embedder.EnsureReadyAsync(CancellationToken.None);
        if (!ready) return;

        await using var _ = embedder;

        var store1 = new DiskVectorStore(DiskVectorStoreOptions.Default(root));

        await store1.AddManyAsync("demo", new[]
        {
            VectorDocument.Create("k8s", "kubernetes autoscaling with prometheus",
                embedder.Embed("kubernetes autoscaling with prometheus"),
                new Dictionary<string, string> { { "topic", "devops" } }),
            VectorDocument.Create("food", "recipe for raclette", embedder.Embed("recipe for raclette"),
                new Dictionary<string, string> { { "topic", "food" } }),
        });

        var store2 = new DiskVectorStore(DiskVectorStoreOptions.Default(root));

        var hits = await store2.SearchAsync("demo",
            queryVector: embedder.Embed("how to autoscale pods"),
            options: new SearchOptions(TopK: 2, Filter: MetadataFilter.Eq("topic", "devops"),
                Mode: SearchMode.VectorOnly));

        Assert.True(hits.Count > 0);
        Assert.Equal("k8s", hits[0].Id);
    }

    [Fact]
    public async Task Hybrid_With_Text_Reads_Text_From_Disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-vectordbdisk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var embedder = new HfMiniLmEmbedder(Path.Combine(root, ".hf-cache"));
        var ready = await embedder.EnsureReadyAsync(CancellationToken.None);
        if (!ready) return;

        await using var _ = embedder;

        var store = new DiskVectorStore(DiskVectorStoreOptions.Default(root));

        await store.AddManyAsync("demo", new[]
        {
            VectorDocument.Create("r1", "install slimfaas on kubernetes",
                embedder.Embed("install slimfaas on kubernetes"),
                new Dictionary<string, string> { { "topic", "devops" } }),
            VectorDocument.Create("r2", "recipe for raclette", embedder.Embed("recipe for raclette"),
                new Dictionary<string, string> { { "topic", "food" } }),
            VectorDocument.Create("r3", "kubernetes autoscaling with prometheus",
                embedder.Embed("kubernetes autoscaling with prometheus"),
                new Dictionary<string, string> { { "topic", "devops" } }),
        });

        var hits = await store.SearchAsync("demo",
            queryText: "kubernetes autoscaling",
            queryVector: embedder.Embed("autoscale pods kubernetes"),
            options: new SearchOptions(TopK: 2, Mode: SearchMode.Hybrid, Filter: MetadataFilter.Eq("topic", "devops")));

        Assert.True(hits.Count > 0);
        Assert.All(hits, h => Assert.Equal("devops", h.Metadata["topic"]));
    }



    [Fact]
    public async Task Update_Then_Delete_Robust_With_Compaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-vectordbdisk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var embedder = new HfMiniLmEmbedder(Path.Combine(root, ".hf-cache"));
        var ready = await embedder.EnsureReadyAsync(CancellationToken.None);
        if (!ready) return;

        await using var _ = embedder;

        var store = new DiskVectorStore(DiskVectorStoreOptions.Default(root));

        await store.UpsertManyAsync("demo", new[]
        {
            VectorDocument.Create("doc1", "hello world", embedder.Embed("hello world"),
                new Dictionary<string, string> { { "kind", "greet" } }),
            VectorDocument.Create("doc2", "pizza recipe", embedder.Embed("pizza recipe"),
                new Dictionary<string, string> { { "kind", "food" } }),
        });

        await store.UpsertManyAsync("demo", new[]
        {
            VectorDocument.Create("doc1", "hello kubernetes", embedder.Embed("hello kubernetes"),
                new Dictionary<string, string> { { "kind", "devops" } }),
        });

        var hitsDevops = await store.SearchAsync("demo",
            queryVector: embedder.Embed("kubernetes"),
            options: new SearchOptions(TopK: 5, Filter: MetadataFilter.Eq("kind", "devops"),
                Mode: SearchMode.VectorOnly));

        Assert.Contains(hitsDevops, h => h.Id == "doc1");
        Assert.DoesNotContain(hitsDevops, h => h.Id == "doc2");

        var hitsGreet = await store.SearchAsync("demo",
            queryVector: embedder.Embed("hello"),
            options: new SearchOptions(TopK: 5, Filter: MetadataFilter.Eq("kind", "greet"),
                Mode: SearchMode.VectorOnly));

        Assert.DoesNotContain(hitsGreet, h => h.Id == "doc1");

        await store.DeleteManyAsync("demo", new[] { "doc2" });

        var hitsFood = await store.SearchAsync("demo",
            queryVector: embedder.Embed("recipe"),
            options: new SearchOptions(TopK: 5, Filter: MetadataFilter.Eq("kind", "food"),
                Mode: SearchMode.VectorOnly));

        Assert.DoesNotContain(hitsFood, h => h.Id == "doc2");
    }

    [Fact]
    public async Task List_And_Delete_Collection_Works()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-vectordbdisk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var embedder = new HfMiniLmEmbedder(Path.Combine(root, ".hf-cache"));
        var ready = await embedder.EnsureReadyAsync(CancellationToken.None);
        if (!ready) return;

        await using var _ = embedder;

        var store = new DiskVectorStore(DiskVectorStoreOptions.Default(root));

        await store.UpsertManyAsync("c1", new[] { VectorDocument.Create("a", "alpha", embedder.Embed("alpha")) });
        await store.UpsertManyAsync("c2", new[] { VectorDocument.Create("b", "beta", embedder.Embed("beta")) });

        var cols = await store.ListCollectionsAsync();
        Assert.Contains("c1", cols);
        Assert.Contains("c2", cols);

        await store.DeleteCollectionAsync("c1");

        cols = await store.ListCollectionsAsync();
        Assert.DoesNotContain("c1", cols);
        Assert.Contains("c2", cols);
    }


    [Fact]
    public async Task No_Compaction_On_Write_Search_Still_Sees_Updates()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-vectordbdisk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var embedder = new HfMiniLmEmbedder(Path.Combine(root, ".hf-cache"));
        var ready = await embedder.EnsureReadyAsync(CancellationToken.None);
        if (!ready) return;

        await using var _ = embedder;

        var opts = new DiskVectorStoreOptions(root,
            NormalizeVectorsOnInsert: true,
            AutoCompactOnWrite: false,
            MaxOpsBytesBeforeCompaction: 1024 * 1024,
            AutoCompactOnSearchIfOpsTooLarge: false,
            MaxOpsBytesToScanOnSearch: 1024 * 1024);

        var store = new DiskVectorStore(opts);

        await store.UpsertManyAsync("demo", new[]
        {
            VectorDocument.Create("doc1", "hello world", embedder.Embed("hello world"),
                new Dictionary<string, string> { { "kind", "greet" } }),
        });

        // Update without compaction
        await store.UpsertManyAsync("demo", new[]
        {
            VectorDocument.Create("doc1", "hello kubernetes", embedder.Embed("hello kubernetes"),
                new Dictionary<string, string> { { "kind", "devops" } }),
        });

        var hits = await store.SearchAsync("demo",
            queryVector: embedder.Embed("kubernetes"),
            options: new SearchOptions(TopK: 5, Filter: MetadataFilter.Eq("kind", "devops"),
                Mode: SearchMode.VectorOnly));

        Assert.Contains(hits, h => h.Id == "doc1");
    }

}