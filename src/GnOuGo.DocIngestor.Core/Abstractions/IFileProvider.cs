using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Abstractions;

/// <summary>
/// Abstracts file system access for document discovery and opening.
/// CLI implementations provide disk-based access;
/// web scenarios can implement a stream-based version.
/// </summary>
public interface IFileProvider
{
    /// <summary>
    /// Enumerate document sources from the given <paramref name="path"/>.
    /// For disk-based providers, <paramref name="path"/> can be a file or directory.
    /// </summary>
    IAsyncEnumerable<DocumentSource> EnumerateAsync(string path, CancellationToken ct = default);
}


