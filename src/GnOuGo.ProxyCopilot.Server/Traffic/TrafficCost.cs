using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Traffic;

public sealed record CostBreakdown(decimal Input, decimal CachedInput, decimal CacheWriteInput, decimal Output, decimal ReasoningOutput);
public sealed record CostEstimate(string Status, string? Currency, decimal? Amount, CostBreakdown? Breakdown, long? InputTokensAbove);
public sealed record TrafficCostTotal(string Currency, decimal Amount, int Calls, int PartialCalls);

/// <summary>Estimates against configured rates only; never discovers prices or converts currencies.</summary>
public static class TrafficCost
{
    // Capture only numeric usage fields. Untrusted strings and arbitrary upstream
    // metadata must never bypass body redaction through the traffic summary.
    public static JsonObject CaptureUsage(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "prompt_tokens", "completion_tokens" }) CopyCount(source, result, key);
        foreach (var (key, fields) in new[] {
            ("prompt_tokens_details", new[] { "cached_tokens", "cache_write_tokens" }),
            ("completion_tokens_details", new[] { "reasoning_tokens" }) })
        {
            if (!source.ContainsKey(key) || source[key] is null) continue;
            if (source[key] is not JsonObject details) { result[key] = null; continue; }
            var captured = new JsonObject();
            foreach (var field in fields) CopyCount(details, captured, field);
            result[key] = captured;
        }
        if (TryCount(result, "prompt_tokens", false, out var input) && TryCount(result, "completion_tokens", false, out var output)
            && input <= long.MaxValue - output) result["total_tokens"] = input + output;
        return result;
    }

    public static CostEstimate Estimate(ProxyModelOptions model, JsonObject? usage, string callStatus)
    {
        var price = model.Metadata.Pricing;
        long? threshold = null;
        CostEstimate Unknown() => new("unknown", price?.Currency, null, null, threshold);
        if (usage is null || !TryCount(usage, "prompt_tokens", false, out var input)
            || !TryCount(usage, "completion_tokens", false, out var output)) return Unknown();
        foreach (var tier in model.PricingTiers)
            if (input > tier.InputTokensAbove && (threshold is null || tier.InputTokensAbove > threshold))
            { price = tier.Pricing; threshold = tier.InputTokensAbove; }
        if (price?.InputPer1MTokens is not { } inputRate || price.OutputPer1MTokens is not { } outputRate) return Unknown();
        if (!TryDetail(usage, "prompt_tokens_details", "cached_tokens", out var cached)
            || !TryDetail(usage, "prompt_tokens_details", "cache_write_tokens", out var written)
            || !TryDetail(usage, "completion_tokens_details", "reasoning_tokens", out var reasoning)
            || cached > input || written > input - cached || reasoning > output) return Unknown();
        try
        {
            var costs = new CostBreakdown(
                (input - cached - written) / 1_000_000m * inputRate,
                cached / 1_000_000m * (price.CachedInputPer1MTokens ?? inputRate),
                written / 1_000_000m * (price.CacheWriteInputPer1MTokens ?? inputRate),
                (output - reasoning) / 1_000_000m * outputRate,
                reasoning / 1_000_000m * (price.ReasoningOutputPer1MTokens ?? outputRate));
            return new(callStatus == "completed" ? "estimated" : "partial", price.Currency,
                costs.Input + costs.CachedInput + costs.CacheWriteInput + costs.Output + costs.ReasoningOutput, costs, threshold);
        }
        catch (OverflowException) { return Unknown(); }
    }

    private static void CopyCount(JsonObject source, JsonObject target, string key)
    {
        if (source.ContainsKey(key)) target[key] = TryCount(source, key, false, out var count) ? JsonValue.Create(count) : null;
    }

    private static bool TryDetail(JsonObject usage, string group, string key, out long count)
    {
        count = 0;
        return !usage.ContainsKey(group) || usage[group] is JsonObject details && TryCount(details, key, true, out count);
    }

    private static bool TryCount(JsonObject source, string key, bool optional, out long count)
    {
        count = 0;
        if (!source.ContainsKey(key)) return optional;
        return source[key] is JsonValue value && value.TryGetValue<long>(out count) && count >= 0;
    }
}
