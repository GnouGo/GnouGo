namespace DocIngestor.Core.Abstractions;

public interface IEmbeddingModel
{
    string Name { get; }
    int Dimensions { get; }
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

public interface IEmbeddingRouter
{
    IEmbeddingModel Get(string modelName);
}
