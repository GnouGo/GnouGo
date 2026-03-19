using System.Runtime.CompilerServices;
using DocIngestor.Core.Abstractions;

namespace DocIngestor.Cli.IO;

/// <summary>
/// Disk-based implementation of <see cref="IFileProvider"/>.
/// Discovers supported document files and returns seekable <see cref="DocumentSource"/> backed by FileStream.
/// </summary>
public sealed class DiskFileProvider : IFileProvider
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".pptx", ".xlsx", ".md", ".markdown"
    };

    public async IAsyncEnumerable<DocumentSource> EnumerateAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            yield return await OpenFileAsync(path, ct);
        }
        else if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return await OpenFileAsync(file, ct);
            }
        }
    }

    /// <summary>
    /// Opens a single file and returns a <see cref="DocumentSource"/> that owns the stream.
    /// The content is copied into a MemoryStream so the file handle is released immediately.
    /// </summary>
    private static async Task<DocumentSource> OpenFileAsync(string filePath, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            await fs.CopyToAsync(ms, ct);
        }

        ms.Position = 0;

        // Build optional preset metadata with disk-specific info
        var fi = new FileInfo(filePath);
        var presetMeta = new Dictionary<string, string>
        {
            ["disk.fullPath"] = filePath,
            ["disk.createdUtc"] = fi.CreationTimeUtc.ToString("O"),
            ["disk.modifiedUtc"] = fi.LastWriteTimeUtc.ToString("O"),
        };

        return new DocumentSource(
            content: ms,
            fileName: fi.Name,
            contentType: GuessContentType(fi.Extension),
            length: ms.Length,
            presetMetadata: presetMeta,
            ownsStream: true);
    }

    private static string? GuessContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".md" or ".markdown" => "text/markdown",
        _ => null
    };
}

