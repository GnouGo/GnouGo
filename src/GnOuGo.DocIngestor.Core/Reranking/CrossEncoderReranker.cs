using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Reranking;

/// <summary>
/// Generic Cross-Encoder reranker that delegates scoring to an <see cref="IChatScorer"/>.
///
/// <para><b>What is a Cross-Encoder?</b></para>
/// <para>
/// Unlike a Bi-Encoder (used for embedding), which encodes query and document
/// independently into separate vectors and then computes similarity with a dot product,
/// a Cross-Encoder processes the query and document <em>together</em> as a single input.
/// This allows the model to attend across both texts simultaneously, capturing fine-grained
/// interactions (negations, paraphrases, entity references) that are invisible to bi-encoders.
/// </para>
/// <para>
/// The actual LLM call is abstracted behind <see cref="IChatScorer"/>, allowing you to
/// swap providers (OpenAI, Ollama, Azure, local models) without changing the reranking logic.
/// </para>
/// </summary>
public sealed class CrossEncoderReranker : IReranker
{
    private readonly IChatScorer _scorer;
    private readonly int _maxConcurrency;
    private readonly string _name;

    /// <inheritdoc />
    public string Name => _name;

    /// <param name="scorer">The LLM chat scorer implementation to use for cross-encoding.</param>
    /// <param name="maxConcurrency">Max parallel scoring requests (to limit rate / cost).</param>
    /// <param name="name">Reranker name for the registry (defaults to "cross-encoder-{scorer.Name}").</param>
    public CrossEncoderReranker(
        IChatScorer scorer,
        int maxConcurrency = 5,
        string? name = null)
    {
        _scorer = scorer;
        _maxConcurrency = maxConcurrency;
        _name = name ?? $"cross-encoder-{scorer.Name}";
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<VectorSearchResult>> RerankAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        RerankerOptions options,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return Array.Empty<VectorSearchResult>();

        var scores = new double[candidates.Count];
        using var semaphore = new SemaphoreSlim(_maxConcurrency);

        var tasks = new Task[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    scores[idx] = await _scorer.ScoreAsync(query, candidates[idx].Chunk.Chunk.Text, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);
        }

        await Task.WhenAll(tasks);

        double maxCe = 0;
        for (int i = 0; i < scores.Length; i++)
            if (scores[i] > maxCe) maxCe = scores[i];

        if (maxCe > 0)
        {
            for (int i = 0; i < scores.Length; i++)
                scores[i] /= maxCe;
        }

        var vw = options.VectorWeight;
        var rw = options.RerankWeight;
        var totalWeight = vw + rw;
        if (totalWeight <= 0) { vw = 0.5; rw = 0.5; totalWeight = 1.0; }

        var results = new List<VectorSearchResult>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var blended = (candidates[i].Score * vw + scores[i] * rw) / totalWeight;
            results.Add(new VectorSearchResult(Math.Round(blended, 6), candidates[i].Chunk));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(options.TopK)
            .ToList();
    }
}
