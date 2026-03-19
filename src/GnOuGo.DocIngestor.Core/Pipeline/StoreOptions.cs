namespace DocIngestor.Core.Pipeline;

public sealed record StoreOptions(
    bool EnableStore = false,
    string StoreName = "jsonl",
    string Collection = "default"
);
