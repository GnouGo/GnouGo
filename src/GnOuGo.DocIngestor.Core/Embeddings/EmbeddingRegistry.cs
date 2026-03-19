using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Embeddings;

public sealed class EmbeddingRegistry : IEmbeddingRouter
{
    private readonly Dictionary<string, IEmbeddingModel> _models;

    public EmbeddingRegistry(IEnumerable<IEmbeddingModel> models)
    {
        _models = new Dictionary<string, IEmbeddingModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
            _models[m.Name] = m;
    }

    public IEmbeddingModel Get(string modelName)
        => _models.TryGetValue(modelName, out var m)
            ? m
            : throw new KeyNotFoundException($"Embedding model '{modelName}' is not registered.");
}

/// <summary>Deterministic embedder for local/dev/tests.</summary>
public sealed class HashEmbeddingModel : IEmbeddingModel
{
    public string Name { get; }
    public int Dimensions { get; }

    public HashEmbeddingModel(string name = "hash-384", int dimensions = 384)
    {
        Name = name;
        Dimensions = dimensions;
    }

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // quick deterministic vector - no crypto dependency to keep it fast
        unchecked
        {
            var vec = new float[Dimensions];
            int h = 17;
            foreach (var ch in text ?? "")
                h = h * 31 + ch;

            for (int i = 0; i < vec.Length; i++)
            {
                h = h * 1103515245 + 12345;
                vec[i] = ((h >> 16) & 0x7fff) / 16384f - 1f;
            }
            return ValueTask.FromResult(vec);
        }
    }
}
