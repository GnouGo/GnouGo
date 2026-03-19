namespace GnOuGo.VectorDbDisk;

internal static class MetadataPostings
{
    public static void AppendDocId(string postingPath, int docId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(postingPath)!);

        int last = TryReadLastDocId(postingPath);
        int delta = (last < 0) ? docId : (docId - last);
        if (delta < 0)
            throw new InvalidOperationException("docId must be appended in increasing order.");

        using var fs = File.Open(postingPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        VarInt.WriteUInt32(fs, (uint)delta);
    }

    public static PostingsEnumerator OpenEnumerator(string postingPath)
        => new(postingPath);

    private static int TryReadLastDocId(string postingPath)
    {
        if (!File.Exists(postingPath)) return -1;

        using var fs = File.Open(postingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int cur = -1;
        int acc = 0;
        while (fs.Position < fs.Length)
        {
            uint delta = VarInt.ReadUInt32(fs);
            acc += checked((int)delta);
            cur = acc;
        }
        return cur;
    }
}

internal sealed class PostingsEnumerator : IDisposable
{
    private readonly FileStream _fs;
    private int _acc = 0;

    public int Current { get; private set; }

    public PostingsEnumerator(string postingPath)
    {
        _fs = File.Open(postingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public bool MoveNext()
    {
        if (_fs.Position >= _fs.Length) return false;

        uint delta = VarInt.ReadUInt32(_fs);
        _acc += checked((int)delta);
        Current = _acc;
        return true;
    }

    public void Dispose() => _fs.Dispose();
}
