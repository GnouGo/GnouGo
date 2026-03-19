using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

/// <summary>Optional capability for vector stores that support vector search.</summary>
public interface IVectorSearchStore : IVectorStore
{
    ValueTask<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collection,
        float[] queryVector,
        int topK = 10,
        CancellationToken ct = default);
}
