using System.Text.Json.Nodes;
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
            .Resolve(_options?.ResolveProvider(providerType)?.ResolvedType ?? providerType, model)
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
            providerType: _options?.ResolveProvider(providerType)?.ResolvedType ?? providerType);
    }

    public ModelUsageCostEstimate? EstimateReportedCostWithCurrency(string? model, long inputTokens, long outputTokens,
        JsonNode? usage, string? providerType = null)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var route = _options?.ResolveProvider(providerType);
        var metadata = new LLMModelMetadataResolver(_options).Resolve(route?.ResolvedType ?? providerType, model);
        if (metadata.Pricing is not { InputPer1MTokens: >= 0, OutputPer1MTokens: >= 0 } pricing) return null;
        var details = usage?["input_tokens_details"] ?? usage?["prompt_tokens_details"];
        long Count(JsonNode? node) => node is JsonValue value &&
            long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0 ? count : 0;
        var cached = Count(details?["cached_tokens"] ?? usage?["cached_input_tokens"]);
        var writes = Count(details?["cache_write_tokens"] ?? usage?["cache_write_tokens"]);
        var tier = usage?["service_tier"]?.ToString() ?? route?.RequestPolicy.BillingServiceTier ?? "standard";
        return new(ModelMetadataCatalog.EstimateUsageCost(pricing, inputTokens, outputTokens, cached, tier,
            route?.RequestPolicy.BillingRegionalProcessing == true, writes), pricing.Currency);
    }

    public ModelUsageCostEstimate? EstimateCostWithCurrency(
        string? model,
        long? inputTokens = null,
        long? outputTokens = null,
        string? providerType = null)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var pricing = new LLMModelMetadataResolver(_options)
            .Resolve(_options?.ResolveProvider(providerType)?.ResolvedType ?? providerType, model)
            .Pricing;
        if (pricing?.InputPer1MTokens is null or < 0
            || pricing.OutputPer1MTokens is null or < 0)
        {
            return null;
        }

        var currency = pricing.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (currency.Length != 3 || currency.Any(static character => character is < 'A' or > 'Z'))
            return null;

        var route = _options?.ResolveProvider(providerType);
        var reservePricing = ModelMetadataCatalog.Clone(new() { Pricing = pricing }).Pricing!;
        reservePricing.InputPer1MTokens = new[] { pricing.InputPer1MTokens, pricing.CacheWriteInputPer1MTokens, pricing.CacheWrite12hInputPer1MTokens }.Max();
        var amount = ModelMetadataCatalog.EstimateUsageCost(reservePricing, inputTokens ?? 0, outputTokens ?? 0,
            serviceTier: route?.RequestPolicy.BillingServiceTier ?? "standard", regional: route?.RequestPolicy.BillingRegionalProcessing == true);
        return new ModelUsageCostEstimate(amount, currency);
    }
}
