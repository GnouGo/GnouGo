using System.Text;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DocIngestor.Cli.Debugging;

public static class DebugArtifactWriter
{
    public static async Task WriteAsync(
        string sourceFileName,
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyList<ImageArtifact> images,
        string debugRoot,
        CancellationToken ct = default)
    {
        // Per-file folder name: <filename>-<sha12 or ticks>
        var safeBase = SanitizeFileName(Path.GetFileNameWithoutExtension(sourceFileName));
        var sha12 = TryGetSha12FromChunks(chunks);
        var perFileDir = Path.Combine(debugRoot, $"{safeBase}-{sha12}");

        Directory.CreateDirectory(perFileDir);

        // 1) chunks.yaml (NO vectors)
        var chunksDto = new DebugChunksDumpDto(
            SourceName: sourceFileName,
            GeneratedUtc: DateTime.UtcNow.ToString("O"),
            Chunks: chunks.Select(c => new DebugChunkDto(
                ChunkId: c.ChunkId,
                DocumentId: c.DocumentId,
                SectionId: c.SectionId,
                Index: c.Index,
                Text: c.Text,
                Metadata: c.Metadata,
                Markdown: c.Markdown,
                CsvLike: c.CsvLike
            )).ToList()
        );

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var chunksYamlPath = Path.Combine(perFileDir, "chunks.yaml");
        var chunksYaml = serializer.Serialize(chunksDto);
        await File.WriteAllTextAsync(chunksYamlPath, chunksYaml, new UTF8Encoding(false), ct);

        // 2) Source file copy is no longer done here (stream-based sources don't guarantee disk access)

        // 3) images/
        var imagesDir = Path.Combine(perFileDir, "images");
        if (Directory.Exists(imagesDir))
            Directory.Delete(imagesDir, recursive: true);
        Directory.CreateDirectory(imagesDir);

        var manifest = new List<DebugImageDto>(capacity: images.Count);

        int i = 0;
        foreach (var img in images)
        {
            ct.ThrowIfCancellationRequested();

            i++;
            var ext = GuessImageExtension(img.ContentType, img.Bytes);
            var baseName = img.Name;
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = img.Id;

            baseName = SanitizeFileName(baseName);

            // Prefix by page/slide if present
            var pagePrefix = img.PageNumber is int p ? $"p{p:000}_" : "";
            var fileName = $"{pagePrefix}{i:000}_{Truncate(baseName, 80)}{ext}";
            var dest = Path.Combine(imagesDir, fileName);

            bool saved = false;
            if (img.Bytes is not null && img.Bytes.Length > 0)
            {
                await File.WriteAllBytesAsync(dest, img.Bytes, ct);
                saved = true;
            }

            manifest.Add(new DebugImageDto(
                Id: img.Id,
                PageNumber: img.PageNumber,
                SectionId: img.SectionId,
                Name: img.Name,
                ContentType: img.ContentType,
                Width: img.Width,
                Height: img.Height,
                LengthBytes: img.LengthBytes,
                SavedFileName: saved ? fileName : null,
                Metadata: img.Metadata
            ));
        }

        // manifest.json (bonus utile)
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);
        await File.WriteAllTextAsync(Path.Combine(imagesDir, "manifest.json"), manifestJson, new UTF8Encoding(false), ct);
    }

    private static string TryGetSha12FromChunks(IReadOnlyList<TextChunk> chunks)
    {
        // Best-effort: sha256 est généralement dans les metadata
        foreach (var c in chunks)
        {
            if (c.Metadata is not null &&
                c.Metadata.TryGetValue("sha256", out var sha) &&
                !string.IsNullOrWhiteSpace(sha))
            {
                return sha.Length >= 12 ? sha.Substring(0, 12) : sha;
            }
        }

        // fallback
        return Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    private static string GuessImageExtension(string? contentType, byte[]? bytes)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
            if (contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (contentType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase)) return ".bmp";
            if (contentType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase)) return ".tiff";
        }

        // sniff headers (best effort)
        if (bytes is { Length: >= 12 })
        {
            // PNG
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            // JPEG
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
            // GIF
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
            // WEBP ("RIFF....WEBP")
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        }

        return ".bin";
    }

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "item";
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        // Windows: évite aussi ':' qui peut apparaître dans des IDs
        s = s.Replace(':', '_');
        return s.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    private sealed record DebugChunksDumpDto(
        string SourceName,
        string GeneratedUtc,
        IReadOnlyList<DebugChunkDto> Chunks
    );

    private sealed record DebugChunkDto(
        string ChunkId,
        string DocumentId,
        string SectionId,
        int Index,
        string Text,
        IReadOnlyDictionary<string, string> Metadata,
        string? Markdown,
        string? CsvLike
    );

    private sealed record DebugImageDto(
        string Id,
        int? PageNumber,
        string? SectionId,
        string? Name,
        string? ContentType,
        int? Width,
        int? Height,
        long? LengthBytes,
        string? SavedFileName,
        IReadOnlyDictionary<string, string> Metadata
    );
}
