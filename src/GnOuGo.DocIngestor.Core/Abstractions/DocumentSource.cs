using System.Security.Cryptography;

namespace DocIngestor.Core.Abstractions;

/// <summary>
/// Represents a document to be ingested — decoupled from the file system.
/// The <see cref="Content"/> stream should be seekable (callers must buffer if needed).
/// </summary>
public sealed class DocumentSource : IDisposable, IAsyncDisposable
{
    /// <summary>Seekable stream with the document bytes.</summary>
    public Stream Content { get; }

    /// <summary>Logical file name (e.g. "report.pdf"). Used for routing and metadata — never as a disk path.</summary>
    public string FileName { get; }

    /// <summary>Optional MIME content type (e.g. "application/pdf"). Helps routing when extension is ambiguous.</summary>
    public string? ContentType { get; }

    /// <summary>Length in bytes (may be null for non-seekable sources before buffering).</summary>
    public long? Length { get; }

    /// <summary>Optional pre-set metadata (e.g. upload context, user-provided tags).</summary>
    public IReadOnlyDictionary<string, string>? PresetMetadata { get; }

    private readonly bool _ownsStream;

    public DocumentSource(
        Stream content,
        string fileName,
        string? contentType = null,
        long? length = null,
        IReadOnlyDictionary<string, string>? presetMetadata = null,
        bool ownsStream = false)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ContentType = contentType;
        Length = length ?? (content.CanSeek ? content.Length : null);
        PresetMetadata = presetMetadata;
        _ownsStream = ownsStream;
    }

    /// <summary>
    /// Ensures the underlying stream is seekable. If it is not, copies into a MemoryStream and returns a new
    /// <see cref="DocumentSource"/> that owns the buffered stream.
    /// </summary>
    public static async Task<DocumentSource> EnsureSeekableAsync(DocumentSource source, CancellationToken ct = default)
    {
        if (source.Content.CanSeek)
            return source;

        var ms = new MemoryStream();
        await source.Content.CopyToAsync(ms, ct);
        ms.Position = 0;

        return new DocumentSource(
            ms,
            source.FileName,
            source.ContentType,
            ms.Length,
            source.PresetMetadata,
            ownsStream: true);
    }

    /// <summary>Rewind the stream to the beginning (safe no-op when not seekable).</summary>
    public void Rewind()
    {
        if (Content.CanSeek)
            Content.Position = 0;
    }

    /// <summary>Compute SHA-256 of the content, then rewind.</summary>
    public string ComputeSha256()
    {
        Content.Position = 0;
        var hash = SHA256.HashData(Content);
        Content.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_ownsStream)
            Content.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsStream)
            await Content.DisposeAsync();
    }
}

