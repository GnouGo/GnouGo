using System.Text;

namespace GnOuGo.VectorDbDisk;

internal sealed class DiskCollectionWriter : IDisposable
{
    private readonly string _dir;
    private readonly string _headerPath;
    private readonly string _docsPath;
    private readonly string _offsetsPath;

    private readonly FileStream _docs;
    private readonly FileStream _offsets;

    public int VectorSize { get; }
    public long DocCount { get; private set; }

    public DiskCollectionWriter(string rootPath, string collection, int vectorSize, bool createIfMissing)
    {
        _dir = DiskPaths.CollectionDir(rootPath, collection);
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, DiskPaths.MetaDir));

        _headerPath = Path.Combine(_dir, DiskPaths.HeaderFile);
        _docsPath = Path.Combine(_dir, DiskPaths.DocsFile);
        _offsetsPath = Path.Combine(_dir, DiskPaths.OffsetsFile);

        if (File.Exists(_headerPath))
        {
            using var fs = File.OpenRead(_headerPath);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            if (fs.Length >= 8)
            {
                _ = br.ReadInt32(); // formatVersion
                VectorSize = br.ReadInt32();
            }
            else
            {
                VectorSize = br.ReadInt32();
            }
        }
        else
        {
            if (!createIfMissing) throw new InvalidOperationException("Collection does not exist.");
            VectorSize = vectorSize;
            using var fs = File.Open(_headerPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
            bw.Write(2); // formatVersion
            bw.Write(VectorSize);
        }

        _docs = File.Open(_docsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _docs.Seek(0, SeekOrigin.End);

        _offsets = File.Open(_offsetsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _offsets.Seek(0, SeekOrigin.End);

        if (_offsets.Length % 8 != 0) throw new InvalidDataException("offsets.bin is corrupted.");
        DocCount = _offsets.Length / 8;
    }

    public int Append(VectorDocument doc, bool normalizeVectorsOnInsert)
    {
        if (doc.Vector.Length != VectorSize)
            throw new InvalidOperationException($"Vector size mismatch. Expected {VectorSize}, got {doc.Vector.Length}.");

        if (normalizeVectorsOnInsert)
            MathEx.NormalizeInPlace(doc.Vector);

        long offset = _docs.Position;

        Span<byte> off = stackalloc byte[8];
        BitConverter.TryWriteBytes(off, offset);
        _offsets.Write(off);

        using var bw = new BinaryWriter(_docs, Encoding.UTF8, leaveOpen: true);

        long lenPos = _docs.Position;
        bw.Write(0); // placeholder
        long start = _docs.Position;

        WriteUtf8String(bw, doc.Id);
        WriteUtf8String(bw, doc.Text);

        bw.Write(doc.Metadata.Count);
        foreach (var (k, v) in doc.Metadata)
        {
            WriteUtf8String(bw, k);
            WriteUtf8String(bw, v);
        }

        bw.Write(VectorSize);
        for (int i = 0; i < VectorSize; i++)
            bw.Write(doc.Vector[i]);

        long end = _docs.Position;
        int recordLen = checked((int)(end - start));

        long cur = _docs.Position;
        _docs.Position = lenPos;
        bw.Write(recordLen);
        _docs.Position = cur;

        int docId = checked((int)DocCount);
        DocCount++;
        return docId;
    }

    private static void WriteUtf8String(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    public void Flush()
    {
        _docs.Flush(flushToDisk: true);
        _offsets.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        _docs.Dispose();
        _offsets.Dispose();
    }
}
