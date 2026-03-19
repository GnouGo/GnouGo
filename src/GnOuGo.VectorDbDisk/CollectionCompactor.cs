using System.Text;

namespace GnOuGo.VectorDbDisk;

internal static class CollectionCompactor
{
    public static void Compact(string rootPath, string collection, bool normalizeVectorsOnInsert, CancellationToken ct)
    {
        var cdir = DiskPaths.CollectionDir(rootPath, collection);
        Directory.CreateDirectory(cdir);
        Directory.CreateDirectory(Path.Combine(cdir, DiskPaths.MetaDir));

        int vectorSize = ReadVectorSizeFromHeader(cdir);

        var opsPath = OperationLog.OpsPath(cdir);
        if (!File.Exists(opsPath))
        {
            EnsureMaterializedEmpty(cdir);
            return;
        }

        // Load pending ops into a map (expected to be bounded by threshold).
        // Value null => tombstone, else latest doc.
        var pending = new Dictionary<string, VectorDocument?>(StringComparer.Ordinal);
        foreach (var (type, doc, del) in OperationLog.ReadAll(opsPath))
        {
            ct.ThrowIfCancellationRequested();
            if (type == OpType.Upsert)
            {
                if (doc is null) throw new InvalidDataException("Upsert without doc.");
                if (doc.Vector.Length != vectorSize) throw new InvalidDataException("Vector size mismatch in ops log.");
                pending[doc.Id] = doc;
            }
            else
            {
                pending[del!] = null;
            }
        }

        // Write to temp dir
        var tmpDir = Path.Combine(cdir, ".__compact");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(Path.Combine(tmpDir, DiskPaths.MetaDir));

        WriteHeader(tmpDir, vectorSize);

        var docsPath = Path.Combine(tmpDir, DiskPaths.DocsFile);
        var offsetsPath = Path.Combine(tmpDir, DiskPaths.OffsetsFile);

        using var docsFs = File.Open(docsPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        using var offsetsFs = File.Open(offsetsPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        int outDocId = 0;

        // 1) Copy from existing snapshot (if any), applying pending ops overlays
        var snapshotDocsPath = Path.Combine(cdir, DiskPaths.DocsFile);
        var snapshotOffsetsPath = Path.Combine(cdir, DiskPaths.OffsetsFile);

        if (File.Exists(snapshotDocsPath) && File.Exists(snapshotOffsetsPath))
        {
            using var reader = new DiskCollectionReader(rootPath, collection);

            for (int docId = 0; docId < reader.DocCount; docId++)
            {
                ct.ThrowIfCancellationRequested();

                var doc = reader.ReadDocument(docId);

                if (pending.TryGetValue(doc.Id, out var updated))
                {
                    // updated == null => deleted
                    if (updated is null)
                    {
                        pending.Remove(doc.Id);
                        continue;
                    }

                    // write updated version instead of old
                    WriteDocAndPostings(updated, docsFs, offsetsFs, tmpDir, vectorSize, normalizeVectorsOnInsert, outDocId);
                    outDocId++;
                    pending.Remove(doc.Id);
                    continue;
                }

                // unchanged
                WriteDocAndPostings(doc, docsFs, offsetsFs, tmpDir, vectorSize, normalizeVectorsOnInsert, outDocId);
                outDocId++;
            }
        }

        // 2) Append remaining upserts that are new docs
        foreach (var kv in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (kv.Value is null) continue; // tombstone for unknown id: ignore
            WriteDocAndPostings(kv.Value, docsFs, offsetsFs, tmpDir, vectorSize, normalizeVectorsOnInsert, outDocId);
            outDocId++;
        }

        docsFs.Flush(true);
        offsetsFs.Flush(true);

        // After compaction, reset ops.bin (empty): snapshot now contains full state
        File.WriteAllBytes(OperationLog.OpsPath(tmpDir), Array.Empty<byte>());

        ReplaceCollectionFiles(cdir, tmpDir);

        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
    }

    private static void WriteDocAndPostings(
        VectorDocument d,
        FileStream docsFs,
        FileStream offsetsFs,
        string tmpDir,
        int vectorSize,
        bool normalizeVectorsOnInsert,
        int docId)
    {
        if (normalizeVectorsOnInsert)
            MathEx.NormalizeInPlace(d.Vector);

        long offset = docsFs.Position;
        Span<byte> off = stackalloc byte[8];
        BitConverter.TryWriteBytes(off, offset);
        offsetsFs.Write(off);

        using var bw = new BinaryWriter(docsFs, Encoding.UTF8, leaveOpen: true);

        long lenPos = docsFs.Position;
        bw.Write(0);
        long start = docsFs.Position;

        WriteUtf8(bw, d.Id);
        WriteUtf8(bw, d.Text);

        bw.Write(d.Metadata.Count);
        foreach (var (k, v) in d.Metadata)
        {
            WriteUtf8(bw, k);
            WriteUtf8(bw, v);
        }

        bw.Write(vectorSize);
        for (int i = 0; i < vectorSize; i++) bw.Write(d.Vector[i]);

        long end = docsFs.Position;
        int recordLen = checked((int)(end - start));
        long cur = docsFs.Position;
        docsFs.Position = lenPos;
        bw.Write(recordLen);
        docsFs.Position = cur;

        foreach (var (k, v) in d.Metadata)
        {
            var p = DiskPaths.PostingPath(tmpDir, k, v);
            MetadataPostings.AppendDocId(p, docId);
        }
    }

    public static void WriteHeader(string cdir, int vectorSize)
    {
        var headerPath = Path.Combine(cdir, DiskPaths.HeaderFile);
        using var fs = File.Open(headerPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
        bw.Write(2);
        bw.Write(vectorSize);
    }

    private static int ReadVectorSizeFromHeader(string cdir)
    {
        var header = Path.Combine(cdir, DiskPaths.HeaderFile);
        if (!File.Exists(header))
            throw new InvalidOperationException("Collection header not found. Create the collection by inserting at least one document.");

        using var fs = File.OpenRead(header);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
        if (fs.Length >= 8)
        {
            _ = br.ReadInt32(); // version
            return br.ReadInt32();
        }
        return br.ReadInt32();
    }

    private static void EnsureMaterializedEmpty(string cdir)
    {
        var docs = Path.Combine(cdir, DiskPaths.DocsFile);
        var offsets = Path.Combine(cdir, DiskPaths.OffsetsFile);
        if (!File.Exists(docs)) File.WriteAllBytes(docs, Array.Empty<byte>());
        if (!File.Exists(offsets)) File.WriteAllBytes(offsets, Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(cdir, DiskPaths.MetaDir));
    }

    private static void ReplaceCollectionFiles(string cdir, string tmpDir)
    {
        SafeDeleteFile(Path.Combine(cdir, DiskPaths.DocsFile));
        SafeDeleteFile(Path.Combine(cdir, DiskPaths.OffsetsFile));
        SafeDeleteFile(OperationLog.OpsPath(cdir));
        SafeDeleteFile(Path.Combine(cdir, DiskPaths.HeaderFile));

        var metaDir = Path.Combine(cdir, DiskPaths.MetaDir);
        if (Directory.Exists(metaDir)) Directory.Delete(metaDir, recursive: true);

        File.Move(Path.Combine(tmpDir, DiskPaths.DocsFile), Path.Combine(cdir, DiskPaths.DocsFile));
        File.Move(Path.Combine(tmpDir, DiskPaths.OffsetsFile), Path.Combine(cdir, DiskPaths.OffsetsFile));
        File.Move(OperationLog.OpsPath(tmpDir), OperationLog.OpsPath(cdir));
        File.Move(Path.Combine(tmpDir, DiskPaths.HeaderFile), Path.Combine(cdir, DiskPaths.HeaderFile));
        Directory.Move(Path.Combine(tmpDir, DiskPaths.MetaDir), metaDir);
    }

    private static void SafeDeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void WriteUtf8(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }
}
