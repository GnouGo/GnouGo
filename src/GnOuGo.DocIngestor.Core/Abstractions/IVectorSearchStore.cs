using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

/// <summary>Optional capability for vector stores that support vector search.</summary>
public interface IVectorSearchStore : IVectorStore
{
    /// <summary>Finds the chunks whose vectors are closest to a query vector.</summary>
    /// <param name="collection">Collection to search.</param>
    /// <param name="queryVector">Query embedding.</param>
    /// <param name="topK">Maximum number of matches.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matches ordered by descending similarity.</returns>
    ValueTask<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collection,
        float[] queryVector,
        int topK = 10,
        CancellationToken ct = default);
}
