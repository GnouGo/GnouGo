using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Estimates model usage cost from the GnOuGo AI model metadata catalog.
/// </summary>
public sealed class ModelMetadataUsageCostEstimator : IModelUsageCostEstimator
{
    public decimal? EstimateCost(
        string? model,
        long? inputTokens = null,
        long? outputTokens = null,
        string? providerType = null)
        => ModelMetadataCatalog.EstimateCost(
            model,
            inputTokens,
            outputTokens,
            providerType: providerType);
}
