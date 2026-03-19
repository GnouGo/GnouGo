using System.Text;

namespace GnOuGo.VectorDbDisk;

internal enum OpType : byte
{
    Upsert = 0,
    Delete = 1,
}

internal static class OperationLog
{
    public const string OpsFile = "ops.bin";

    public static string OpsPath(string collectionDir) => Path.Combine(collectionDir, OpsFile);

    public static void AppendUpsert(string opsPath, VectorDocument doc, int expectedVectorSize, bool normalizeVector)
    {
        if (doc.Vector.Length != expectedVectorSize)
            throw new InvalidOperationException($"Vector size mismatch. Expected {expectedVectorSize}, got {doc.Vector.Length}.");

        if (normalizeVector)
            MathEx.NormalizeInPlace(doc.Vector);

        using var fs = File.Open(opsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        bw.Write((byte)OpType.Upsert);
        WriteUtf8String(bw, doc.Id);
        WriteUtf8String(bw, doc.Text);

        bw.Write(doc.Metadata.Count);
        foreach (var (k, v) in doc.Metadata)
        {
            WriteUtf8String(bw, k);
            WriteUtf8String(bw, v);
        }

        bw.Write(expectedVectorSize);
        for (int i = 0; i < expectedVectorSize; i++)
            bw.Write(doc.Vector[i]);
    }

    public static void AppendDelete(string opsPath, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id must be non-empty.", nameof(id));

        using var fs = File.Open(opsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        bw.Write((byte)OpType.Delete);
        WriteUtf8String(bw, id);
    }

    public static IEnumerable<(OpType type, VectorDocument? doc, string? deleteId)> ReadAll(string opsPath)
    {
        if (!File.Exists(opsPath)) yield break;

        using var fs = File.Open(opsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        while (fs.Position < fs.Length)
        {
            var t = (OpType)br.ReadByte();
            if (t == OpType.Upsert)
            {
                var id = ReadUtf8String(br);
                var text = ReadUtf8String(br);

                int metaCount = br.ReadInt32();
                var md = new Dictionary<string, string>(metaCount, StringComparer.Ordinal);
                for (int i = 0; i < metaCount; i++)
                {
                    var k = ReadUtf8String(br);
                    var v = ReadUtf8String(br);
                    md[k] = v;
                }

                int vsize = br.ReadInt32();
                var vec = new float[vsize];
                for (int i = 0; i < vsize; i++) vec[i] = br.ReadSingle();

                yield return (t, new VectorDocument(id, text, vec, md), null);
            }
            else if (t == OpType.Delete)
            {
                var id = ReadUtf8String(br);
                yield return (t, null, id);
            }
            else
            {
                throw new InvalidDataException($"Unknown OpType {(byte)t}.");
            }
        }
    }

    internal static void WriteUtf8String(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    internal static string ReadUtf8String(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > 128 * 1024 * 1024) throw new InvalidDataException("Invalid string length.");
        var bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }
}
