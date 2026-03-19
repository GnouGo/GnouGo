namespace GnOuGo.VectorDbDisk;

public sealed class DiskVectorStore
{
    private readonly DiskVectorStoreOptions _options;

    public DiskVectorStore(DiskVectorStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(_options.RootPath);
    }

    public Task AddManyAsync(string collection, IEnumerable<VectorDocument> documents, CancellationToken ct = default)
        => UpsertManyAsync(collection, documents, ct);

    /// <summary>
    /// Upsert documents by Id.
    /// Robust write path:
    /// - append operations to ops.bin
    /// - optionally compact when ops.bin exceeds threshold (not on every write)
    /// </summary>
    public async Task UpsertManyAsync(string collection, IEnumerable<VectorDocument> documents, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection name must be provided.", nameof(collection));

        var docs = documents?.ToList() ?? throw new ArgumentNullException(nameof(documents));
        if (docs.Count == 0) return;

        int vectorSize = docs[0].Vector.Length;
        for (int i = 1; i < docs.Count; i++)
            if (docs[i].Vector.Length != vectorSize)
                throw new ArgumentException("All vectors in the batch must have the same size.", nameof(documents));

        var cdir = DiskPaths.CollectionDir(_options.RootPath, collection);
        Directory.CreateDirectory(cdir);
        Directory.CreateDirectory(Path.Combine(cdir, DiskPaths.MetaDir));

        // Ensure header exists and matches
        var headerPath = Path.Combine(cdir, DiskPaths.HeaderFile);
        if (!File.Exists(headerPath))
        {
            CollectionCompactor.WriteHeader(cdir, vectorSize);

            // Ensure materialized files exist for search
            var docsPath = Path.Combine(cdir, DiskPaths.DocsFile);
            var offsetsPath = Path.Combine(cdir, DiskPaths.OffsetsFile);
            if (!File.Exists(docsPath)) File.WriteAllBytes(docsPath, Array.Empty<byte>());
            if (!File.Exists(offsetsPath)) File.WriteAllBytes(offsetsPath, Array.Empty<byte>());
        }
        else
        {
            using var r = new DiskCollectionReader(_options.RootPath, collection);
            if (r.VectorSize != vectorSize)
                throw new InvalidOperationException($"Vector size mismatch for collection '{collection}'. Expected {r.VectorSize}, got {vectorSize}.");
        }

        var opsPath = OperationLog.OpsPath(cdir);
        foreach (var d in docs)
        {
            ct.ThrowIfCancellationRequested();
            OperationLog.AppendUpsert(opsPath, d, vectorSize, normalizeVector: _options.NormalizeVectorsOnInsert);
        }

        await MaybeAutoCompactAsync(collection, ct);
    }

    /// <summary>
    /// Delete documents by Id (tombstones) in ops.bin.
    /// Optionally compact when ops.bin exceeds threshold.
    /// </summary>
    public async Task DeleteManyAsync(string collection, IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection name must be provided.", nameof(collection));
        var list = ids?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList()
                   ?? throw new ArgumentNullException(nameof(ids));
        if (list.Count == 0) return;

        var cdir = DiskPaths.CollectionDir(_options.RootPath, collection);
        if (!Directory.Exists(cdir)) return;

        var headerPath = Path.Combine(cdir, DiskPaths.HeaderFile);
        if (!File.Exists(headerPath)) return;

        var opsPath = OperationLog.OpsPath(cdir);
        foreach (var id in list)
        {
            ct.ThrowIfCancellationRequested();
            OperationLog.AppendDelete(opsPath, id);
        }

        await MaybeAutoCompactAsync(collection, ct);
    }

    public Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dirs = Directory.EnumerateDirectories(_options.RootPath)
            .Where(d => File.Exists(Path.Combine(d, DiskPaths.HeaderFile)))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList()!;

        return Task.FromResult((IReadOnlyList<string>)dirs);
    }

    public Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection name must be provided.", nameof(collection));
        ct.ThrowIfCancellationRequested();

        var cdir = DiskPaths.CollectionDir(_options.RootPath, collection);
        if (Directory.Exists(cdir))
            Directory.Delete(cdir, recursive: true);

        return Task.CompletedTask;
    }

    public Task CompactAsync(string collection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection name must be provided.", nameof(collection));
        CollectionCompactor.Compact(_options.RootPath, collection, _options.NormalizeVectorsOnInsert, ct);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string collection,
        float[]? queryVector = null,
        string? queryText = null,
        SearchOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("Collection name must be provided.", nameof(collection));
        options ??= new SearchOptions();

        bool hasVector = queryVector is not null && queryVector.Length > 0;
        bool hasText = !string.IsNullOrWhiteSpace(queryText);

        if (options.Mode == SearchMode.VectorOnly && !hasVector)
            throw new ArgumentException("queryVector is required for VectorOnly mode.");
        if (options.Mode == SearchMode.TextOnly && !hasText)
            throw new ArgumentException("queryText is required for TextOnly mode.");
        if (options.Mode == SearchMode.Hybrid && !(hasVector || hasText))
            throw new ArgumentException("queryVector or queryText is required for Hybrid mode.");

        var cdir = DiskPaths.CollectionDir(_options.RootPath, collection);
        var opsPath = OperationLog.OpsPath(cdir);

        // If ops is too large, optionally compact before search
        if (_options.AutoCompactOnSearchIfOpsTooLarge && File.Exists(opsPath))
        {
            var len = new FileInfo(opsPath).Length;
            if (len > _options.MaxOpsBytesToScanOnSearch)
                await CompactAsync(collection, ct);
        }

        // Load pending ops (overlay) for correctness (new docs + overrides + deletes)
        var overlay = LoadPendingOpsBounded(opsPath, _options.MaxOpsBytesToScanOnSearch);

        using var reader = new DiskCollectionReader(_options.RootPath, collection);

        float[]? q = null;
        if (hasVector)
        {
            if (queryVector!.Length != reader.VectorSize)
                throw new InvalidOperationException($"Vector size mismatch. Expected {reader.VectorSize}, got {queryVector.Length}.");

            q = (float[])queryVector.Clone();
            if (options.NormalizeQueryVector) MathEx.NormalizeInPlace(q);
        }

        var filter = options.Filter ?? MetadataFilter.True();

        // We'll collect topK by Id (dedupe), using a min-heap by score.
        var heap = new PriorityQueue<(string id, string text, IReadOnlyDictionary<string, string> md, double score, double? vScore, double? tScore), double>();

        void Offer(string id, string text, IReadOnlyDictionary<string, string> md, double score, double? vScore, double? tScore)
        {
            if (heap.Count < options.TopK)
                heap.Enqueue((id, text, md, score, vScore, tScore), score);
            else if (heap.TryPeek(out _, out var min) && score > min)
            {
                heap.Dequeue();
                heap.Enqueue((id, text, md, score, vScore, tScore), score);
            }
        }

        var qtoks = hasText ? Tokenize(queryText!) : Array.Empty<string>();

        // 1) Scan snapshot candidates via postings prefilter
        using var candidates = BuildCandidateEnumerator(filter, cdir, (int)reader.DocCount);

        int scanned = 0;

        while (candidates.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            int docId = candidates.Current;

            scanned++;
            if (scanned > options.MaxCandidates) break;

            var header = reader.ReadHeaderFields(docId);

            // Apply overlay (update/delete)
            if (overlay.TryGetValue(header.id, out var ov))
            {
                if (ov is null) continue; // deleted
                header = (ov.Id, ov.Text, ov.Metadata); // use updated fields
            }

            // Re-check filter against latest metadata (snapshot postings may be stale after updates)
            if (!MetadataMatcher.Matches(filter, header.metadata))
                continue;

            double? vScore = null;
            double? tScore = null;

            if (options.Mode != SearchMode.TextOnly && hasVector)
            {
                float[] vec;
                if (overlay.TryGetValue(header.id, out var ov2) && ov2 is not null)
                    vec = ov2.Vector;
                else
                    vec = reader.ReadVector(docId);

                vScore = (double)MathEx.Dot(q!, vec);
            }

            if (options.Mode != SearchMode.VectorOnly && hasText)
            {
                tScore = (double)TokenOverlapScore(qtoks, header.text);
            }

            double score = ComputeScore(options, vScore, tScore, qtoks.Length);
            Offer(header.id, header.text, header.metadata, score, vScore, tScore);
        }

        // 2) Also consider docs that exist only in overlay (new inserts or updated docs not found by snapshot postings)
        foreach (var kv in overlay)
        {
            ct.ThrowIfCancellationRequested();
            var doc = kv.Value;
            if (doc is null) continue;

            if (!MetadataMatcher.Matches(filter, doc.Metadata))
                continue;

            double? vScore = null;
            double? tScore = null;

            if (options.Mode != SearchMode.TextOnly && hasVector)
                vScore = (double)MathEx.Dot(q!, doc.Vector);

            if (options.Mode != SearchMode.VectorOnly && hasText)
                tScore = (double)TokenOverlapScore(qtoks, doc.Text);

            double score = ComputeScore(options, vScore, tScore, qtoks.Length);
            Offer(doc.Id, doc.Text, doc.Metadata, score, vScore, tScore);
        }

        // Extract heap to sorted list
        var tmp = new List<SearchHit>(heap.Count);
        while (heap.Count > 0)
        {
            var it = heap.Dequeue();
            tmp.Add(new SearchHit(it.id, it.text, it.md, it.score, it.vScore, it.tScore));
        }
        tmp.Sort((a, b) => b.Score.CompareTo(a.Score));

        return await Task.FromResult((IReadOnlyList<SearchHit>)tmp);
    }

    private static double ComputeScore(SearchOptions options, double? vScore, double? tScore, int queryTokenCount)
    {
        if (options.Mode == SearchMode.VectorOnly)
        {
            return MathEx.Clamp01(((vScore ?? 0) + 1) / 2);
        }

        if (options.Mode == SearchMode.TextOnly)
        {
            return queryTokenCount == 0 ? 0 : (tScore ?? 0) / queryTokenCount;
        }

        double v = vScore is null ? 0 : MathEx.Clamp01((vScore.Value + 1) / 2);
        double t = queryTokenCount == 0 ? 0 : (tScore ?? 0) / queryTokenCount;
        return (options.VectorWeight * v) + (options.TextWeight * t);
    }

    private async Task MaybeAutoCompactAsync(string collection, CancellationToken ct)
    {
        if (!_options.AutoCompactOnWrite) return;

        var cdir = DiskPaths.CollectionDir(_options.RootPath, collection);
        var opsPath = OperationLog.OpsPath(cdir);
        if (!File.Exists(opsPath)) return;

        var len = new FileInfo(opsPath).Length;
        if (len >= _options.MaxOpsBytesBeforeCompaction)
            await CompactAsync(collection, ct);
    }

    private static Dictionary<string, VectorDocument?> LoadPendingOpsBounded(string opsPath, long maxBytes)
    {
        var map = new Dictionary<string, VectorDocument?>(StringComparer.Ordinal);

        if (!File.Exists(opsPath)) return map;

        var len = new FileInfo(opsPath).Length;
        if (len > maxBytes)
            throw new InvalidOperationException($"ops.bin is too large to scan ({len} bytes). Increase MaxOpsBytesToScanOnSearch or run CompactAsync().");

        foreach (var (type, doc, del) in OperationLog.ReadAll(opsPath))
        {
            if (type == OpType.Upsert)
                map[doc!.Id] = doc!;
            else
                map[del!] = null;
        }

        return map;
    }

    private static IDocIdEnumerator BuildCandidateEnumerator(IMetadataFilter filter, string collectionDir, int docCount)
    {
        return filter switch
        {
            TrueFilter => new RangeEnumerator(0, docCount),
            EqualsFilter eq => BuildEquals(eq, collectionDir),
            InFilter inf => BuildIn(inf, collectionDir),
            AndFilter and => new IntersectEnumerator(BuildCandidateEnumerator(and.Left, collectionDir, docCount), BuildCandidateEnumerator(and.Right, collectionDir, docCount)),
            OrFilter or => new UnionEnumerator(BuildCandidateEnumerator(or.Left, collectionDir, docCount), BuildCandidateEnumerator(or.Right, collectionDir, docCount)),
            _ => new RangeEnumerator(0, docCount)
        };

        static IDocIdEnumerator BuildEquals(EqualsFilter eq, string cdir)
        {
            var p = DiskPaths.PostingPath(cdir, eq.Key, eq.Value);
            if (!File.Exists(p)) return EmptyEnumerator.Instance;
            return new PostingEnumeratorAdapter(p);
        }

        static IDocIdEnumerator BuildIn(InFilter inf, string cdir)
        {
            IDocIdEnumerator? acc = null;

            foreach (var v in inf.Values)
            {
                var p = DiskPaths.PostingPath(cdir, inf.Key, v);
                if (!File.Exists(p)) continue;

                var e = (IDocIdEnumerator)new PostingEnumeratorAdapter(p);
                acc = acc is null ? e : new UnionEnumerator(acc, e);
            }

            return acc ?? EmptyEnumerator.Instance;
        }
    }

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var list = new List<string>(16);
        Span<char> buf = stackalloc char[128];
        int len = 0;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (len < buf.Length) buf[len++] = char.ToLowerInvariant(ch);
            }
            else
            {
                if (len > 0) { list.Add(new string(buf[..len])); len = 0; }
            }
        }
        if (len > 0) list.Add(new string(buf[..len]));
        return list.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static int TokenOverlapScore(string[] queryTokens, string text)
    {
        if (queryTokens.Length == 0) return 0;
        var dtoks = Tokenize(text);
        int c = 0;
        foreach (var t in queryTokens)
            if (dtoks.Contains(t, StringComparer.Ordinal)) c++;
        return c;
    }
}
