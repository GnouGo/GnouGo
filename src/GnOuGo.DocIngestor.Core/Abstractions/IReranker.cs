using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

/// <summary>
/// Reranks search results by re-scoring them against the original query text.
/// Implementations may use BM25, cross-encoder models, LLM-based reranking, etc.
/// </summary>
public interface IReranker
{
    /// <summary>Unique name used to select this reranker (e.g. "bm25", "cross-encoder").</summary>
    string Name { get; }

    /// <summary>
    /// Re-score and re-order search results.
    /// The returned list must be sorted by final score descending.
    /// </summary>
    ValueTask<IReadOnlyList<VectorSearchResult>> RerankAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        RerankerOptions options,
        CancellationToken ct = default);
}

/// <summary>Options that control reranking behavior.</summary>
public sealed record RerankerOptions(
    int TopK = 10,
    double VectorWeight = 0.5,
    double RerankWeight = 0.5);

/// <summary>Routes to a named <see cref="IReranker"/> implementation.</summary>
public interface IRerankerRouter
{
    IReranker Get(string name);
    IReadOnlyList<string> Available { get; }
}

