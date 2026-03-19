using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Chunking;

/// <summary>
/// Size-driven splitter with a hierarchy:
/// - paragraph
/// - sentence
/// - hard cut
/// </summary>
public sealed class RecursiveChunker : IChunker
{
    private readonly ITokenCounter _tokens;

    public RecursiveChunker(ITokenCounter tokenCounter)
        => _tokens = tokenCounter;

    public ChunkingMode Mode => ChunkingMode.Recursive;

    public ValueTask<IReadOnlyList<TextChunk>> ChunkAsync(ExtractedDocument doc, ChunkSizePolicy policy, CancellationToken ct = default)
    {
        var chunks = new List<TextChunk>();

        foreach (var section in doc.Sections)
        {
            ct.ThrowIfCancellationRequested();

            var units = SplitIntoParagraphs(section.Text);
            var packed = Pack(units, policy);

            int idx = 0;
            foreach (var p in packed)
            {
                var meta = BuildChunkMetadata(doc, section);
                chunks.Add(new TextChunk(
                    ChunkId: $"{doc.DocumentId}:{section.SectionId}:rec:{idx}",
                    DocumentId: doc.DocumentId,
                    SectionId: section.SectionId,
                    Index: idx,
                    Text: p,
                    Metadata: meta
                ));
                idx++;
            }
        }

        return ValueTask.FromResult<IReadOnlyList<TextChunk>>(chunks);
    }

    private IReadOnlyList<string> Pack(IReadOnlyList<string> units, ChunkSizePolicy policy)
    {
        var target = PickAllowed(policy.TargetTokens, policy.AllowedTargetTokens);
        var max = policy.MaxTokens;
        var min = policy.MinTokens;
        var overlap = Math.Max(0, policy.OverlapTokens);

        var results = new List<string>();
        var buf = new List<string>();
        int bufTokens = 0;

        int i = 0;
        while (i < units.Count)
        {
            var u = units[i];
            var t = _tokens.CountTokens(u);

            // if single unit too big, split further (sentences then hard)
            if (t > max)
            {
                // flush buffer
                Flush();
                foreach (var part in SplitRecursively(u, max))
                {
                    results.Add(part);
                }
                i++;
                continue;
            }

            if (bufTokens + t <= target || buf.Count == 0)
            {
                buf.Add(u);
                bufTokens += t;
                i++;
                continue;
            }

            Flush();

            // apply overlap by carrying last N tokens (approx by last paragraphs)
            if (overlap > 0 && results.Count > 0)
            {
                var carry = CarryOverlap(results[^1], overlap);
                if (!string.IsNullOrWhiteSpace(carry))
                {
                    buf.Add(carry);
                    bufTokens = _tokens.CountTokens(carry);
                }
            }
        }

        Flush(final: true);

        // merge tiny chunks with previous
        for (int k = 1; k < results.Count; k++)
        {
            if (_tokens.CountTokens(results[k]) < min)
            {
                results[k - 1] = results[k - 1].TrimEnd() + "\n\n" + results[k].TrimStart();
                results.RemoveAt(k);
                k--;
            }
        }

        return results;

        void Flush(bool final = false)
        {
            if (buf.Count == 0) return;
            var text = string.Join("\n\n", buf).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                results.Add(text);
            buf.Clear();
            bufTokens = 0;
        }
    }

    private IEnumerable<string> SplitRecursively(string text, int maxTokens)
    {
        // sentence split
        var sentences = SplitIntoSentences(text).ToList();
        if (sentences.Count > 1)
        {
            foreach (var chunk in HardPack(sentences, maxTokens))
                yield return chunk;
            yield break;
        }

        // hard cut fallback (char-index based, iterator-safe)
        int start = 0;
        while (start < text.Length)
        {
            var remaining = text.Substring(start);
            if (_tokens.CountTokens(remaining) <= maxTokens)
            {
                yield return remaining.Trim();
                yield break;
            }

            // approximate split point by chars
            var cut = Math.Min(text.Length, start + Math.Max(200, (maxTokens * 4)));
            var piece = text.Substring(start, cut - start);

            // ensure we don't exceed maxTokens too much; back off if needed
            while (_tokens.CountTokens(piece) > maxTokens && piece.Length > 200)
                piece = piece.Substring(0, piece.Length - 100);

            yield return piece.Trim();
            start += Math.Max(1, piece.Length);
        }
    }

    private IEnumerable<string> HardPack(IReadOnlyList<string> units, int maxTokens)
    {
        var buf = new List<string>();
        int tok = 0;

        foreach (var u in units)
        {
            var t = _tokens.CountTokens(u);
            if (tok + t <= maxTokens || buf.Count == 0)
            {
                buf.Add(u);
                tok += t;
                continue;
            }

            yield return string.Join(" ", buf).Trim();
            buf.Clear();
            tok = 0;

            buf.Add(u);
            tok = t;
        }

        if (buf.Count > 0)
            yield return string.Join(" ", buf).Trim();
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

    private static IEnumerable<string> SplitIntoSentences(string text)
    {
        // simple punctuation-based sentence splitter (AOT-safe)
        var s = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        if (string.IsNullOrWhiteSpace(s)) yield break;

        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is '.' or '!' or '?' or ';')
            {
                var len = i - start + 1;
                var seg = s.Substring(start, len).Trim();
                if (seg.Length > 0) yield return seg;
                start = i + 1;
            }
        }

        var tail = s.Substring(start).Trim();
        if (tail.Length > 0) yield return tail;
    }

    private int PickAllowed(int target, int[]? allowed)
    {
        if (allowed is null || allowed.Length == 0) return target;
        int best = allowed[0];
        int bestDist = Math.Abs(best - target);
        foreach (var a in allowed)
        {
            var d = Math.Abs(a - target);
            if (d < bestDist) { best = a; bestDist = d; }
        }
        return best;
    }

    private string CarryOverlap(string lastChunk, int overlapTokens)
    {
        // Keep overlapTokens from the end, but implemented as "take last ~N chars"
        // (token-exact overlap isn't required; it's a practical approximation).
        var chars = Math.Min(lastChunk.Length, overlapTokens * 6);
        return lastChunk.Substring(Math.Max(0, lastChunk.Length - chars)).Trim();
    }

    private static IReadOnlyDictionary<string, string> BuildChunkMetadata(ExtractedDocument doc, ExtractedSection section)
    {
        var meta = new Dictionary<string, string>(doc.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sectionTitle"] = section.Title
        };

        if (section.PageNumber is not null)
            meta["pageNumber"] = section.PageNumber.Value.ToString();

        foreach (var kv in section.Metadata)
            meta[$"section.{kv.Key}"] = kv.Value;

        return meta;
    }
}
