using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Chunking;

/// <summary>
/// Embedding-driven merge:
/// 1) create small paragraph units
/// 2) embed ALL units across ALL sections in a single batch API call (fast!)
/// 3) merge adjacent units if cosine similarity >= threshold and token budget allows
/// </summary>
public sealed class SemanticChunker : IChunker
{
    private readonly ITokenCounter _tokens;
    private readonly IEmbeddingRouter _router;
    private readonly string _modelName;
    private readonly double _similarityThreshold;

    /// <summary>Maximum number of parallel embedding batch calls.</summary>
    private const int MaxParallelBatches = 4;

    /// <summary>Maximum texts per single EmbedBatchAsync call (matches OpenAI limit).</summary>
    private const int MaxTextsPerBatch = 2048;

    public SemanticChunker(
        ITokenCounter tokenCounter,
        IEmbeddingRouter embeddingRouter,
        string embeddingModelName,
        double similarityThreshold = 0.80,
        GenAiTelemetry? telemetry = null)
    {
        _tokens = tokenCounter;
        _router = embeddingRouter;
        _modelName = embeddingModelName;
        _similarityThreshold = similarityThreshold;
        // telemetry is accepted for API compat but not used here:
        // the underlying IEmbeddingModel handles its own gen_ai tracing.
        _ = telemetry;
    }

    public ChunkingMode Mode => ChunkingMode.Semantic;

    public async ValueTask<IReadOnlyList<TextChunk>> ChunkAsync(ExtractedDocument doc, ChunkSizePolicy policy, CancellationToken ct = default)
    {
        var model = _router.Get(_modelName);
        var unitTargetTokens = Math.Max(64, policy.TargetTokens / 4);

        // ── Phase 1: Collect all pre-grouped texts from all sections ──
        // Each entry: (sectionIndex, list of grouped texts)
        var sectionGroups = new List<(int SectionIdx, ExtractedSection Section, IReadOnlyList<string> GroupedTexts)>();
        var allTexts = new List<string>(); // flat list for batch embedding
        var textOffsets = new List<int>();  // offset into allTexts for each section

        for (int s = 0; s < doc.Sections.Count; s++)
        {
            var section = doc.Sections[s];
            var paras = SplitIntoParagraphs(section.Text);
            if (paras.Count == 0) continue;

            var groupedTexts = PreGroupParagraphs(paras, unitTargetTokens);
            if (groupedTexts.Count == 0) continue;

            textOffsets.Add(allTexts.Count);
            sectionGroups.Add((s, section, groupedTexts));

            for (int g = 0; g < groupedTexts.Count; g++)
                allTexts.Add(groupedTexts[g]);
        }

        if (allTexts.Count == 0) return Array.Empty<TextChunk>();

        // ── Phase 2: Batch embed ALL texts — split into parallel sub-batches ──
        // NOTE: We do NOT create gen_ai.embedding traces here because the underlying
        // IEmbeddingModel (e.g. OpenAiCompatibleEmbeddingModel) already creates its own
        // properly attributed traces with gen_ai.request.model set to the real model name.
        // We only create a lightweight "semantic_chunker.embed" span for structural context.
        var allEmbeddings = new float[allTexts.Count][];

        if (allTexts.Count <= MaxTextsPerBatch)
        {
            // Single batch — most common case
            using var activity = GenAiTelemetry.GetActivitySource()
                .StartActivity("semantic_chunker.embed_batch");
            activity?.SetTag("semantic_chunker.total_units", allTexts.Count);
            activity?.SetTag("semantic_chunker.total_sections", sectionGroups.Count);
            activity?.SetTag("semantic_chunker.model", model.Name);

            var embeddings = await model.EmbedBatchAsync(allTexts, ct);
            for (int i = 0; i < embeddings.Count; i++)
                allEmbeddings[i] = embeddings[i];
        }
        else
        {
            // Split into sub-batches and run them in parallel
            var batches = new List<(int Offset, IReadOnlyList<string> Texts)>();
            for (int offset = 0; offset < allTexts.Count; offset += MaxTextsPerBatch)
            {
                int count = Math.Min(MaxTextsPerBatch, allTexts.Count - offset);
                batches.Add((offset, allTexts.GetRange(offset, count)));
            }

            using var semaphore = new SemaphoreSlim(MaxParallelBatches);
            var tasks = batches.Select(batch => EmbedBatchWithSemaphoreAsync(
                model, batch.Offset, batch.Texts, allEmbeddings, semaphore, ct)).ToArray();

            await Task.WhenAll(tasks);
        }

        // ── Phase 3: Build chunks from embeddings (CPU-only, very fast) ──
        var chunks = new List<TextChunk>();
        for (int sg = 0; sg < sectionGroups.Count; sg++)
        {
            ct.ThrowIfCancellationRequested();
            var (_, section, groupedTexts) = sectionGroups[sg];
            int embOffset = textOffsets[sg];

            var units = new List<Unit>(groupedTexts.Count);
            for (int g = 0; g < groupedTexts.Count; g++)
            {
                var tok = _tokens.CountTokens(groupedTexts[g]);
                if (tok == 0) continue;
                units.Add(new Unit(groupedTexts[g], tok, allEmbeddings[embOffset + g]));
            }

            var max = policy.MaxTokens;
            var idx = 0;
            var i = 0;

            while (i < units.Count)
            {
                var buf = new List<string>();
                int bufTokens = 0;

                var bufEmbedding = (float[])units[i].Embedding.Clone();
                int embCount = 1;

                buf.Add(units[i].Text);
                bufTokens += units[i].Tokens;
                i++;

                while (i < units.Count)
                {
                    var next = units[i];
                    if (bufTokens + next.Tokens > max)
                        break;

                    var sim = Cosine(bufEmbedding, next.Embedding);
                    if (sim < _similarityThreshold)
                        break;

                    // merge
                    buf.Add(next.Text);
                    bufTokens += next.Tokens;
                    embCount++;
                    for (int k = 0; k < bufEmbedding.Length; k++)
                        bufEmbedding[k] = bufEmbedding[k] + (next.Embedding[k] - bufEmbedding[k]) / embCount;

                    i++;
                }

                var text = string.Join("\n\n", buf).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var meta = new Dictionary<string, string>(doc.Metadata, StringComparer.OrdinalIgnoreCase)
                    {
                        ["sectionTitle"] = section.Title,
                        ["embeddingModel"] = model.Name,
                        ["semanticThreshold"] = _similarityThreshold.ToString("0.00"),
                    };

                    if (section.PageNumber is not null)
                        meta["pageNumber"] = section.PageNumber.Value.ToString();

                    foreach (var kv in section.Metadata)
                        meta["section." + kv.Key] = kv.Value;

                    chunks.Add(new TextChunk(
                        ChunkId: doc.DocumentId + ":" + section.SectionId + ":sem:" + idx,
                        DocumentId: doc.DocumentId,
                        SectionId: section.SectionId,
                        Index: idx,
                        Text: text,
                        Metadata: meta
                    ));

                    idx++;
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Embed a sub-batch with concurrency control via semaphore.
    /// The underlying model's EmbedBatchAsync handles its own OpenTelemetry tracing.
    /// </summary>
    private async Task EmbedBatchWithSemaphoreAsync(
        IEmbeddingModel model,
        int offset,
        IReadOnlyList<string> texts,
        float[][] target,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var embeddings = await model.EmbedBatchAsync(texts, ct);
            for (int i = 0; i < embeddings.Count; i++)
                target[offset + i] = embeddings[i];
        }
        finally
        {
            semaphore.Release();
        }
    }


    private sealed record Unit(string Text, int Tokens, float[] Embedding);

    /// <summary>
    /// Merge adjacent small paragraphs into reasonably-sized units before embedding.
    /// This prevents hundreds of tiny embedding calls — a 4-page Word document typically has
    /// 50-200 paragraphs (many empty/short) which would otherwise cause one API call each.
    /// </summary>
    private IReadOnlyList<string> PreGroupParagraphs(IReadOnlyList<string> paragraphs, int targetTokens)
    {
        var groups = new List<string>();
        var buf = new List<string>();
        int bufTokens = 0;

        foreach (var p in paragraphs)
        {
            var t = _tokens.CountTokens(p);
            if (t == 0) continue;

            // If adding this paragraph would exceed the target and the buffer already has content, flush
            if (bufTokens > 0 && bufTokens + t > targetTokens)
            {
                groups.Add(string.Join("\n\n", buf));
                buf.Clear();
                bufTokens = 0;
            }

            buf.Add(p);
            bufTokens += t;
        }

        if (buf.Count > 0)
            groups.Add(string.Join("\n\n", buf));

        return groups;
    }

    private static IReadOnlyList<string> SplitIntoParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var parts = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var res = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var s = p.Trim();
            if (s.Length > 0) res.Add(s);
        }
        return res;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= 1e-12 ? 0 : dot / denom;
    }
}
