using System.Security.Cryptography;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Metadata;

public static class MetadataDefaults
{
    /// <summary>
    /// Build default metadata from a <see cref="DocumentSource"/> (stream-based, no disk access).
    /// </summary>
    public static Dictionary<string, string> FromSource(DocumentSource source, string mimeType)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fileName"] = source.FileName,
            ["extension"] = Path.GetExtension(source.FileName),
            ["mimeType"] = mimeType,
        };

        if (source.Length is long len)
            meta["sizeBytes"] = len.ToString();

        // Merge any preset metadata from the source (e.g. upload context)
        if (source.PresetMetadata is not null)
        {
            foreach (var kv in source.PresetMetadata)
                meta[kv.Key] = kv.Value;
        }

        return meta;
    }

    /// <summary>
    /// Compute SHA-256 from a seekable stream, then rewind.
    /// </summary>
    public static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Append the SHA-256 hash to an <see cref="ExtractedDocument"/>'s metadata,
    /// computing it from the provided stream.
    /// </summary>
    public static ExtractedDocument WithSha256(ExtractedDocument doc, Stream contentStream)
    {
        var meta = new Dictionary<string, string>(doc.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sha256"] = ComputeSha256(contentStream)
        };
        return doc with { Metadata = meta };
    }
}
