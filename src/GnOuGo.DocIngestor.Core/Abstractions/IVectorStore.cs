using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

/// <summary>Persists embedded chunks in named collections.</summary>
public interface IVectorStore
{
    /// <summary>Gets the stable store name used for routing.</summary>
    string Name { get; }

    /// <summary>Upsert embedded chunks into a collection.</summary>
    ValueTask UpsertAsync(
        string collection,
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken ct = default);
}
