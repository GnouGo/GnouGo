
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Telemetry;

/// <summary>
/// Pricing data for a single model (USD per 1M tokens).
/// </summary>
/// <param name="InputPer1MTokens">Cost in USD per 1 million input tokens.</param>
/// <param name="OutputPer1MTokens">Cost in USD per 1 million output tokens.</param>
public readonly record struct ModelPricing(decimal InputPer1MTokens, decimal OutputPer1MTokens);

/// <summary>
/// Compatibility facade for GenAI model pricing. Builtin pricing now comes from
/// <c>Telemetry/model-metadata.json</c> and user/application overrides can be resolved
/// through the overloads that accept <see cref="LLMOptions" />.
/// </summary>
public static partial class ModelPricingCatalog
{
    /// <summary>
    /// Try to look up the pricing for a model by name (case-insensitive, alias-aware).
    /// </summary>
    public static bool TryGetPricing(string modelName, out ModelPricing pricing)
    {
        if (GnOuGo.AI.Core.ModelMetadataCatalog.TryGetBuiltinPricing(modelName, out var metadataPricing))
        {
            pricing = new ModelPricing(
                metadataPricing.InputPer1MTokens ?? 0m,
                metadataPricing.OutputPer1MTokens ?? 0m);
            return true;
        }

        pricing = default;
        return false;
    }

    /// <summary>
    /// Try to look up pricing using the full metadata resolver, including external metadata files
    /// and <see cref="LLMOptions.ModelOverrides" /> user overrides.
    /// </summary>
    public static bool TryGetPricing(string modelName, LLMOptions? options, string? providerType, out ModelPricing pricing)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            pricing = default;
            return false;
        }

        var metadata = new LLMModelMetadataResolver(options).Resolve(providerType, modelName);
        if (metadata.Pricing is { } metadataPricing)
        {
            pricing = new ModelPricing(
                metadataPricing.InputPer1MTokens ?? 0m,
                metadataPricing.OutputPer1MTokens ?? 0m);
            return true;
        }

        pricing = default;
        return false;
    }

    /// <summary>
    /// Compute the estimated cost in USD for a given number of input and output tokens.
    /// Returns null if the model is unknown.
    /// </summary>
    public static decimal? EstimateCost(string modelName, int inputTokens, int outputTokens = 0)
    {
        if (!TryGetPricing(modelName, out var pricing))
            return null;

        return inputTokens / 1_000_000m * pricing.InputPer1MTokens
             + outputTokens / 1_000_000m * pricing.OutputPer1MTokens;
    }

    /// <summary>
    /// Compute the estimated cost in USD for a given number of input and output tokens (nullable/long overload).
    /// Returns null if the model is unknown or <paramref name="modelName"/> is null/empty.
    /// </summary>
    public static decimal? EstimateCost(string? modelName, long? inputTokens, long? outputTokens)
    {
        if (string.IsNullOrWhiteSpace(modelName) || !TryGetPricing(modelName, out var pricing))
            return null;

        var input = inputTokens ?? 0;
        var output = outputTokens ?? 0;

        return input / 1_000_000m * pricing.InputPer1MTokens
             + output / 1_000_000m * pricing.OutputPer1MTokens;
    }

    /// <summary>
    /// Compute estimated cost using the full metadata resolver, including user overrides.
    /// Returns null if the model is unknown or has no pricing metadata.
    /// </summary>
    public static decimal? EstimateCost(
        string? modelName,
        long? inputTokens,
        long? outputTokens,
        LLMOptions? options,
        string? providerType = null)
    {
        if (string.IsNullOrWhiteSpace(modelName) || !TryGetPricing(modelName, options, providerType, out var pricing))
            return null;

        var input = inputTokens ?? 0;
        var output = outputTokens ?? 0;

        return input / 1_000_000m * pricing.InputPer1MTokens
             + output / 1_000_000m * pricing.OutputPer1MTokens;
    }
}

