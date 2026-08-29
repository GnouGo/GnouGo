using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Estimates model usage cost from the GnOuGo AI model metadata catalog.
/// </summary>
public sealed class ModelMetadataUsageCostEstimator : IModelUsageCostEstimator
{
    private readonly LLMOptions? _options;

    public ModelMetadataUsageCostEstimator(LLMOptions? options = null)
    {
        _options = options;
    }

    public decimal? EstimateCost(
        string? model,
        long? inputTokens = null,
        long? outputTokens = null,
        string? providerType = null)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var pricing = new LLMModelMetadataResolver(_options)
            .Resolve(providerType, model)
            .Pricing;
        if (pricing?.InputPer1MTokens is null or < 0
            || pricing.OutputPer1MTokens is null or < 0)
        {
            return null;
        }

        return ModelMetadataCatalog.EstimateCost(
            model,
            inputTokens,
            outputTokens,
            _options,
            providerType: providerType);
    }
}
