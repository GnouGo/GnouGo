namespace GnOuGo.VectorDbDisk;

internal static class DiskPaths
{
    public const string HeaderFile = "header.bin";
    public const string DocsFile = "docs.bin";
    public const string OffsetsFile = "offsets.bin";
    public const string MetaDir = "meta";

    public static string CollectionDir(string root, string collection) =>
        Path.Combine(root, Sanitize(collection));

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "collection" : sanitized;
    }

    public static string SanitizeTerm(string s)
    {
        var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var res = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(res) ? "_" : res;
    }

    public static string PostingPath(string collectionDir, string key, string value)
    {
        var k = SanitizeTerm(key);
        var v = SanitizeTerm(value);
        return Path.Combine(collectionDir, MetaDir, k, v + ".post");
    }
}
