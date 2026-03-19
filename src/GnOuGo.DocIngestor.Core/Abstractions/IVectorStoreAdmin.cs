namespace DocIngestor.Core.Abstractions;

/// <summary>
/// Optional capability for vector stores that support administrative operations
/// (delete by document, list collections, list documents).
/// </summary>
public interface IVectorStoreAdmin
{
    /// <summary>Delete all vectors for a given document in a collection.</summary>
    ValueTask<int> DeleteByDocumentAsync(string collection, string documentId, CancellationToken ct = default);

    /// <summary>Delete all vectors whose document_id starts with the given prefix (e.g. a file name).</summary>
    ValueTask<int> DeleteByDocumentPrefixAsync(string collection, string documentIdPrefix, CancellationToken ct = default);

    /// <summary>List all distinct collection names.</summary>
    ValueTask<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default);

    /// <summary>List all distinct document IDs in a collection.</summary>
    ValueTask<IReadOnlyList<string>> ListDocumentsAsync(string collection, CancellationToken ct = default);
}

