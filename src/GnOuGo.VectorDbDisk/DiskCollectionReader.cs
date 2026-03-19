using System.Text;

namespace GnOuGo.VectorDbDisk;

internal sealed class DiskCollectionReader : IDisposable
{
    private readonly string _dir;
    private readonly string _headerPath;
    private readonly string _docsPath;
    private readonly string _offsetsPath;

    private readonly FileStream _docs;
    private readonly FileStream _offsets;

    public int VectorSize { get; }
    public long DocCount { get; }

    public DiskCollectionReader(string rootPath, string collection)
    {
        _dir = DiskPaths.CollectionDir(rootPath, collection);
        _headerPath = Path.Combine(_dir, DiskPaths.HeaderFile);
        _docsPath = Path.Combine(_dir, DiskPaths.DocsFile);
        _offsetsPath = Path.Combine(_dir, DiskPaths.OffsetsFile);

        if (!File.Exists(_headerPath)) throw new InvalidOperationException("Collection does not exist.");

        using (var fs = File.OpenRead(_headerPath))
        using (var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
        {
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

        _docs = File.Open(_docsPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        _offsets = File.Open(_offsetsPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);

        if (_offsets.Length % 8 != 0) throw new InvalidDataException("offsets.bin is corrupted.");
        DocCount = _offsets.Length / 8;
    }

    public long ReadOffset(int docId)
    {
        long pos = (long)docId * 8L;
        if (pos < 0 || pos + 8 > _offsets.Length) throw new ArgumentOutOfRangeException(nameof(docId));

        Span<byte> buf = stackalloc byte[8];
        _offsets.Position = pos;
        int n = _offsets.Read(buf);
        if (n != 8) throw new EndOfStreamException();
        return BitConverter.ToInt64(buf);
    }

    public float[] ReadVector(int docId)
    {
        long offset = ReadOffset(docId);
        _docs.Position = offset;

        using var br = new BinaryReader(_docs, Encoding.UTF8, leaveOpen: true);

        int recordLen = br.ReadInt32();
        if (recordLen <= 0) throw new InvalidDataException("Invalid record length.");

        _ = ReadUtf8String(br); // id
        _ = ReadUtf8String(br); // text

        int metaCount = br.ReadInt32();
        for (int i = 0; i < metaCount; i++)
        {
            _ = ReadUtf8String(br);
            _ = ReadUtf8String(br);
        }

        int vectorSize = br.ReadInt32();
        if (vectorSize != VectorSize) throw new InvalidDataException("VectorSize mismatch inside record.");

        var vec = new float[VectorSize];
        for (int i = 0; i < VectorSize; i++) vec[i] = br.ReadSingle();
        return vec;
    }

    public (string id, string text, IReadOnlyDictionary<string, string> metadata) ReadHeaderFields(int docId)
    {
        long offset = ReadOffset(docId);
        _docs.Position = offset;

        using var br = new BinaryReader(_docs, Encoding.UTF8, leaveOpen: true);

        int recordLen = br.ReadInt32();
        if (recordLen <= 0) throw new InvalidDataException("Invalid record length.");

        string id = ReadUtf8String(br);
        string text = ReadUtf8String(br);

        int metaCount = br.ReadInt32();
        var md = new Dictionary<string, string>(metaCount, StringComparer.Ordinal);
        for (int i = 0; i < metaCount; i++)
        {
            var k = ReadUtf8String(br);
            var v = ReadUtf8String(br);
            md[k] = v;
        }

        return (id, text, md);
    }



public VectorDocument ReadDocument(int docId)
{
    long offset = ReadOffset(docId);
    _docs.Position = offset;

    using var br = new BinaryReader(_docs, Encoding.UTF8, leaveOpen: true);

    int recordLen = br.ReadInt32();
    if (recordLen <= 0) throw new InvalidDataException("Invalid record length.");

    string id = ReadUtf8String(br);
    string text = ReadUtf8String(br);

    int metaCount = br.ReadInt32();
    var md = new Dictionary<string, string>(metaCount, StringComparer.Ordinal);
    for (int i = 0; i < metaCount; i++)
    {
        var k = ReadUtf8String(br);
        var v = ReadUtf8String(br);
        md[k] = v;
    }

    int vectorSize = br.ReadInt32();
    if (vectorSize != VectorSize) throw new InvalidDataException("VectorSize mismatch inside record.");

    var vec = new float[VectorSize];
    for (int i = 0; i < VectorSize; i++) vec[i] = br.ReadSingle();

    return new VectorDocument(id, text, vec, md);
}

    private static string ReadUtf8String(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > 128 * 1024 * 1024) throw new InvalidDataException("Invalid string length.");
        var bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        _docs.Dispose();
        _offsets.Dispose();
    }
}
