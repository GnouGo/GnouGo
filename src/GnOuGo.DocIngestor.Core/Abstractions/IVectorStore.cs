using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

public interface IVectorStore
{
    string Name { get; }

    /// <summary>Upsert embedded chunks into a collection.</summary>
    ValueTask UpsertAsync(
        string collection,
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken ct = default);
}
