using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private static void AddPrefilterUsageEvent(
        StepExecutionContext ctx,
        JsonNode? usage,
        string model,
        string? provider,
        string phase,
        string eventName)
    {
        if (usage is not JsonObject usageObject)
            return;

        var attrs = new List<KeyValuePair<string, object?>>
        {
            new("gnougo-flow.plan.phase", phase),
            new("gen_ai.operation.name", "chat"),
            new("gen_ai.system", provider ?? "unknown"),
            new("gen_ai.request.model", model)
        };

        var inputTokens = ReadUsageLong(usageObject, "prompt_tokens", "input_tokens");
        var outputTokens = ReadUsageLong(usageObject, "completion_tokens", "output_tokens");
        var totalTokens = ReadUsageLong(usageObject, "total_tokens", null);

        if (inputTokens.HasValue)
            attrs.Add(new("gen_ai.usage.input_tokens", inputTokens.Value));
        if (outputTokens.HasValue)
            attrs.Add(new("gen_ai.usage.output_tokens", outputTokens.Value));
        if (totalTokens.HasValue)
            attrs.Add(new("gen_ai.usage.total_tokens", totalTokens.Value));

        var estimatedCost = EstimateUsageCost(model, provider, inputTokens, outputTokens);
        if (estimatedCost.HasValue)
            attrs.Add(new("gen_ai.usage.cost", (double)estimatedCost.Value));

        ctx.AddTelemetryEvent(eventName, attrs.ToArray());
    }

    private static void AddUsageAttributes(TelemetrySpanScope span, JsonNode? usage, string model, string? provider)
    {
        if (usage is not JsonObject usageObject)
            return;

        var inputTokens = ReadUsageLong(usageObject, "prompt_tokens", "input_tokens");
        var outputTokens = ReadUsageLong(usageObject, "completion_tokens", "output_tokens");
        var totalTokens = ReadUsageLong(usageObject, "total_tokens", null);

        if (inputTokens.HasValue)
            span.SetAttribute("gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue)
            span.SetAttribute("gen_ai.usage.output_tokens", outputTokens.Value);
        if (totalTokens.HasValue)
            span.SetAttribute("gen_ai.usage.total_tokens", totalTokens.Value);

        var estimatedCost = EstimateUsageCost(model, provider, inputTokens, outputTokens);
        if (estimatedCost.HasValue)
            span.SetAttribute("gen_ai.usage.cost", (double)estimatedCost.Value);
    }

    private static void SetStepUsageTelemetry(StepExecutionContext ctx, JsonNode? usage, string model, string? provider)
    {
        if (usage is not JsonObject usageObject)
            return;

        var inputTokens = ReadUsageLong(usageObject, "prompt_tokens", "input_tokens");
        var outputTokens = ReadUsageLong(usageObject, "completion_tokens", "output_tokens");
        var totalTokens = ReadUsageLong(usageObject, "total_tokens", null);

        if (inputTokens.HasValue)
            ctx.SetTelemetryAttribute("gen_ai.usage.input_tokens", inputTokens.Value);
        if (outputTokens.HasValue)
            ctx.SetTelemetryAttribute("gen_ai.usage.output_tokens", outputTokens.Value);
        if (totalTokens.HasValue)
            ctx.SetTelemetryAttribute("gen_ai.usage.total_tokens", totalTokens.Value);
        else if (inputTokens.HasValue || outputTokens.HasValue)
            ctx.SetTelemetryAttribute("gen_ai.usage.total_tokens", (inputTokens ?? 0) + (outputTokens ?? 0));

        var estimatedCost = EstimateUsageCost(model, provider, inputTokens, outputTokens);
        if (estimatedCost.HasValue)
            ctx.SetTelemetryAttribute("gen_ai.usage.cost", (double)estimatedCost.Value);
    }

    private static decimal? EstimateUsageCost(string model, string? provider, long? inputTokens, long? outputTokens)
    {
        if (inputTokens is null && outputTokens is null)
            return null;

        return ModelMetadataCatalog.EstimateCost(model, inputTokens ?? 0, outputTokens ?? 0, providerType: provider);
    }

    private static long? ReadUsageLong(JsonObject usageObject, string primaryKey, string? secondaryKey)
    {
        if (usageObject.TryGetPropertyValue(primaryKey, out var primary) && primary != null)
            return CoerceLong(primary);
        if (secondaryKey != null && usageObject.TryGetPropertyValue(secondaryKey, out var secondary) && secondary != null)
            return CoerceLong(secondary);
        return null;
    }

    private static long? CoerceLong(JsonNode value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out var parsedLong))
                return parsedLong;
            if (jsonValue.TryGetValue<int>(out var parsedInt))
                return parsedInt;
            if (jsonValue.TryGetValue<double>(out var parsedDouble))
                return (long)parsedDouble;
            if (jsonValue.TryGetValue<string>(out var parsedString)
                && long.TryParse(parsedString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedText))
                return parsedText;
        }

        return null;
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> BuildPlanErrorTelemetryAttributes(
        Exception ex,
        int attempt,
        string phase,
        string? leafName = null)
    {
        var attrs = new List<KeyValuePair<string, object?>>
        {
            new("gnougo-flow.plan.phase", phase),
            new("gnougo-flow.plan.attempt", attempt),
            new("error.type", ex.GetType().Name),
            new("error.message", ex.Message)
        };

        if (!string.IsNullOrWhiteSpace(leafName))
            attrs.Add(new("gnougo-flow.plan.pipeline.leaf_name", leafName));

        if (ex is WorkflowRuntimeException workflowEx)
        {
            attrs.Add(new("error.code", workflowEx.Code));
            if (workflowEx.Details != null)
                attrs.Add(new("error.details", workflowEx.Details.ToJsonString()));
        }

        return attrs;
    }
}
