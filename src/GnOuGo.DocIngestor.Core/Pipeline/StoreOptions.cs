namespace DocIngestor.Core.Pipeline;

/// <summary>Selects whether and where embedded chunks are persisted.</summary>
/// <param name="EnableStore">Whether persistence is enabled.</param>
/// <param name="StoreName">Registered vector-store name.</param>
/// <param name="Collection">Destination collection name.</param>
public sealed record StoreOptions(
    bool EnableStore = false,
    string StoreName = "jsonl",
    string Collection = "default"
);
