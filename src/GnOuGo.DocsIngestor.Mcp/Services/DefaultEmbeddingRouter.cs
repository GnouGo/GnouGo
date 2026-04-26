using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Embeddings;

namespace GnOuGo.DocsIngestor.Mcp.Services;

internal sealed class DefaultEmbeddingRouter : IEmbeddingRouter
{
    private readonly EmbeddingRegistry _registry = new(new IEmbeddingModel[]
    {
        new HashEmbeddingModel("hash-384", 384),
        new HashEmbeddingModel("hash-768", 768),
    });

    public IEmbeddingModel Get(string modelName) => _registry.Get(modelName);
}

