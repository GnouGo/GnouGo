using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Stores;

public sealed class VectorStoreRegistry : IVectorStoreRouter
{
    private readonly Dictionary<string, IVectorStore> _stores;

    public VectorStoreRegistry(IEnumerable<IVectorStore> stores)
    {
        _stores = stores.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IVectorStore Get(string storeName)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            throw new ArgumentException("storeName is required", nameof(storeName));

        if (_stores.TryGetValue(storeName, out var s))
            return s;

        throw new KeyNotFoundException($"Vector store '{storeName}' is not registered.");
    }
}

public interface IVectorStoreRouter
{
    IVectorStore Get(string storeName);
}
