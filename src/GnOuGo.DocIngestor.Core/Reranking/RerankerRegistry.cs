using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Reranking;

/// <summary>
/// Routes to a named <see cref="IReranker"/> by name.
/// </summary>
public sealed class RerankerRegistry : IRerankerRouter
{
    private readonly Dictionary<string, IReranker> _rerankers;

    public RerankerRegistry(IEnumerable<IReranker> rerankers)
    {
        _rerankers = new Dictionary<string, IReranker>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rerankers)
            _rerankers[r.Name] = r;
    }

    public IReranker Get(string name)
        => _rerankers.TryGetValue(name, out var r)
            ? r
            : throw new KeyNotFoundException($"Reranker '{name}' is not registered. Available: {string.Join(", ", _rerankers.Keys)}");

    public IReadOnlyList<string> Available => _rerankers.Keys.ToList();
}

