namespace DocIngestor.Core.Abstractions;

/// <summary>Produces fixed-size vector embeddings for text.</summary>
public interface IEmbeddingModel
{
    /// <summary>Gets the stable model name used for routing and persistence.</summary>
    string Name { get; }
    /// <summary>Gets the number of values in each generated vector.</summary>
    int Dimensions { get; }
    /// <summary>Embeds one text value.</summary>
    /// <param name="text">Text to embed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated vector.</returns>
    ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Embed multiple texts in a single API call (batch).
    /// Default implementation falls back to calling EmbedAsync one by one.
    /// </summary>
    async ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = await EmbedAsync(texts[i], ct);
        }
        return results;
    }
}

/// <summary>Resolves embedding models by their stable names.</summary>
public interface IEmbeddingRouter
{
    /// <summary>Gets the model registered as <paramref name="modelName"/>.</summary>
    /// <param name="modelName">Registered model name.</param>
    /// <returns>The matching embedding model.</returns>
    IEmbeddingModel Get(string modelName);
}
