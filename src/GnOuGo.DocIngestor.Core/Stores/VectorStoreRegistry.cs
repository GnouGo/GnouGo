using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Stores;

/// <summary>Resolves registered vector stores by name.</summary>
public sealed class VectorStoreRegistry : IVectorStoreRouter
{
    private readonly Dictionary<string, IVectorStore> _stores;

    /// <summary>Initializes the registry from available vector stores.</summary>
    /// <param name="stores">Stores to register.</param>
    public VectorStoreRegistry(IEnumerable<IVectorStore> stores)
    {
        _stores = stores.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IVectorStore Get(string storeName)
    {
        if (string.IsNullOrWhiteSpace(storeName))
            throw new ArgumentException("storeName is required", nameof(storeName));

        if (_stores.TryGetValue(storeName, out var s))
            return s;

        throw new KeyNotFoundException($"Vector store '{storeName}' is not registered.");
    }
}

/// <summary>Resolves vector stores through a stable routing boundary.</summary>
public interface IVectorStoreRouter
{
    /// <summary>Gets the store registered under <paramref name="storeName"/>.</summary>
    /// <param name="storeName">Registered store name.</param>
    /// <returns>The matching vector store.</returns>
    IVectorStore Get(string storeName);
}
