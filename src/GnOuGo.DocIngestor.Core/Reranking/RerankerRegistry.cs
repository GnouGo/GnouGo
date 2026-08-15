using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Reranking;

/// <summary>
/// Routes to a named <see cref="IReranker"/> by name.
/// </summary>
public sealed class RerankerRegistry : IRerankerRouter
{
    private readonly Dictionary<string, IReranker> _rerankers;

    /// <summary>Initializes the registry from available rerankers.</summary>
    /// <param name="rerankers">Rerankers to register by name.</param>
    public RerankerRegistry(IEnumerable<IReranker> rerankers)
    {
        _rerankers = new Dictionary<string, IReranker>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rerankers)
            _rerankers[r.Name] = r;
    }

    /// <inheritdoc />
    public IReranker Get(string name)
        => _rerankers.TryGetValue(name, out var r)
            ? r
            : throw new KeyNotFoundException($"Reranker '{name}' is not registered. Available: {string.Join(", ", _rerankers.Keys)}");

    /// <inheritdoc />
    public IReadOnlyList<string> Available => _rerankers.Keys.ToList();
}

