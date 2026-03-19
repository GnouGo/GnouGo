using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Embeddings;
using Xunit;

namespace DocIngestor.Tests;

public sealed class EmbeddingTests
{
    // ── HashEmbeddingModel ───────────────────────────────────────────

    [Fact]
    public async Task HashEmbedding_ReturnsCorrectDimensions()
    {
        var model = new HashEmbeddingModel("hash-384", 384);

        var vector = await model.EmbedAsync("hello world");

        Assert.Equal(384, vector.Length);
    }

    [Fact]
    public async Task HashEmbedding_IsDeterministic()
    {
        var model = new HashEmbeddingModel("hash-768", 768);

        var v1 = await model.EmbedAsync("same text");
        var v2 = await model.EmbedAsync("same text");

        Assert.Equal(v1, v2);
    }

    [Fact]
    public async Task HashEmbedding_DifferentText_DifferentVectors()
    {
        var model = new HashEmbeddingModel("hash-384", 384);

        var v1 = await model.EmbedAsync("cats");
        var v2 = await model.EmbedAsync("dogs");

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public async Task HashEmbedding_EmptyText_DoesNotThrow()
    {
        var model = new HashEmbeddingModel();
        var vector = await model.EmbedAsync("");
        Assert.Equal(384, vector.Length);
    }

    [Fact]
    public async Task HashEmbedding_NullText_DoesNotThrow()
    {
        var model = new HashEmbeddingModel();
        var vector = await model.EmbedAsync(null!);
        Assert.Equal(384, vector.Length);
    }

    [Fact]
    public void HashEmbedding_DefaultNameAndDimensions()
    {
        var model = new HashEmbeddingModel();
        Assert.Equal("hash-384", model.Name);
        Assert.Equal(384, model.Dimensions);
    }

    // ── EmbeddingRegistry ────────────────────────────────────────────

    [Fact]
    public void Registry_Get_ReturnsRegisteredModel()
    {
        var m1 = new HashEmbeddingModel("hash-384", 384);
        var m2 = new HashEmbeddingModel("hash-768", 768);
        var registry = new EmbeddingRegistry(new IEmbeddingModel[] { m1, m2 });

        Assert.Same(m1, registry.Get("hash-384"));
        Assert.Same(m2, registry.Get("hash-768"));
    }

    [Fact]
    public void Registry_Get_IsCaseInsensitive()
    {
        var model = new HashEmbeddingModel("Hash-384", 384);
        var registry = new EmbeddingRegistry(new IEmbeddingModel[] { model });

        Assert.Same(model, registry.Get("hash-384"));
        Assert.Same(model, registry.Get("HASH-384"));
    }

    [Fact]
    public void Registry_Get_ThrowsForUnknownModel()
    {
        var registry = new EmbeddingRegistry(new IEmbeddingModel[]
        {
            new HashEmbeddingModel("hash-384", 384)
        });

        Assert.Throws<KeyNotFoundException>(() => registry.Get("unknown-model"));
    }

    [Fact]
    public void Registry_Empty_ThrowsForAnyGet()
    {
        var registry = new EmbeddingRegistry(Array.Empty<IEmbeddingModel>());
        Assert.Throws<KeyNotFoundException>(() => registry.Get("anything"));
    }
}

