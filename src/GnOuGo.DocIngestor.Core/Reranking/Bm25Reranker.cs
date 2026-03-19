using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Reranking;

/// <summary>
/// BM25-based reranker. Computes a BM25 text-relevance score for each candidate
/// against the query, then blends it with the original vector similarity score.
/// </summary>
public sealed class Bm25Reranker : IReranker
{
    public string Name => "bm25";

    /// <summary>BM25 term-frequency saturation parameter (typically 1.2–2.0).</summary>
    private readonly double _k1;

    /// <summary>BM25 length-normalization parameter (typically 0.75).</summary>
    private readonly double _b;

    public Bm25Reranker(double k1 = 1.5, double b = 0.75)
    {
        _k1 = k1;
        _b = b;
    }

    public ValueTask<IReadOnlyList<VectorSearchResult>> RerankAsync(
        string query,
        IReadOnlyList<VectorSearchResult> candidates,
        RerankerOptions options,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return new ValueTask<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>());

        // Tokenize query
        var queryTerms = Tokenize(query);
        if (queryTerms.Length == 0)
            return new ValueTask<IReadOnlyList<VectorSearchResult>>(candidates);

        // Tokenize all documents and compute stats
        var docTokens = new string[candidates.Count][];
        double totalLength = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            docTokens[i] = Tokenize(candidates[i].Chunk.Chunk.Text);
            totalLength += docTokens[i].Length;
        }
        double avgDl = totalLength / candidates.Count;

        // Document frequency for each query term
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in queryTerms)
        {
            if (df.ContainsKey(term)) continue;
            int count = 0;
            for (int i = 0; i < docTokens.Length; i++)
            {
                if (Contains(docTokens[i], term)) count++;
            }
            df[term] = count;
        }

        int n = candidates.Count;

        // Score each document
        var scored = new List<(VectorSearchResult Result, double Bm25Score)>(n);
        double maxBm25 = 0;

        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();

            var tokens = docTokens[i];
            double dl = tokens.Length;
            double bm25 = 0;

            foreach (var term in queryTerms)
            {
                int tfRaw = CountOccurrences(tokens, term);
                if (tfRaw == 0) continue;

                int docFreq = df.GetValueOrDefault(term, 0);
                // IDF: log((N - df + 0.5) / (df + 0.5) + 1)
                double idf = Math.Log((n - docFreq + 0.5) / (docFreq + 0.5) + 1.0);
                // TF with saturation and length normalization
                double tf = (tfRaw * (_k1 + 1.0)) / (tfRaw + _k1 * (1.0 - _b + _b * (dl / avgDl)));
                bm25 += idf * tf;
            }

            scored.Add((candidates[i], bm25));
            if (bm25 > maxBm25) maxBm25 = bm25;
        }

        // Normalize BM25 scores to [0, 1]
        if (maxBm25 > 0)
        {
            for (int i = 0; i < scored.Count; i++)
            {
                var (result, bm25) = scored[i];
                scored[i] = (result, bm25 / maxBm25);
            }
        }

        // Blend vector score and BM25 score, then sort
        var vw = options.VectorWeight;
        var rw = options.RerankWeight;
        var totalWeight = vw + rw;
        if (totalWeight <= 0) { vw = 0.5; rw = 0.5; totalWeight = 1.0; }

        var results = scored
            .Select(s =>
            {
                var blended = (s.Result.Score * vw + s.Bm25Score * rw) / totalWeight;
                return new VectorSearchResult(Math.Round(blended, 6), s.Result.Chunk);
            })
            .OrderByDescending(r => r.Score)
            .Take(options.TopK)
            .ToList();

        return new ValueTask<IReadOnlyList<VectorSearchResult>>(results);
    }

    // ── Tokenization ────────────────────────────────────────────────

    private static readonly char[] Separators = { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_', '|', '<', '>', '=', '+', '*', '&', '#', '@', '~', '`' };

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1) // skip single chars
            .Select(t => t.ToLowerInvariant())
            .ToArray();
    }

    private static bool Contains(string[] tokens, string term)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int CountOccurrences(string[] tokens, string term)
    {
        int count = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], term, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }
}

