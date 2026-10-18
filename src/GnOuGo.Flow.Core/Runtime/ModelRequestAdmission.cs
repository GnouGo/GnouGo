using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime;

// Journal implementations admit new dispatches after checking completed receipts.
internal interface IModelAdmissionOwner;

internal static class ModelRequestAdmission
{
    internal static long EstimateTokens(LLMRequest request) =>
        ((long)Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 2) / 3 + 256;

    internal static async Task ValidateAsync(LLMRequest request, ILLMCapabilityResolver? resolver, int hostLimit, string stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (resolver is null) throw Unverifiable("Declared model capabilities are unavailable.");
        if (request.StructuredOutputSchema is not null)
        {
            var structured = await resolver.SupportsStructuredOutputAsync(request.Provider, request.Model, ct);
            if (structured is null) throw Unverifiable("Structured Output support is unknown.");
            if (structured != true) throw Incompatible("The selected model does not support required Structured Output.");
        }
        if (request.Reasoning is { } reasoning)
        {
            var levels = await resolver.SupportedReasoningLevelsAsync(request.Provider, request.Model, ct);
            if (levels is null) throw Unverifiable("Supported reasoning settings are unknown.");
            if (!levels.Contains(reasoning, StringComparer.Ordinal)) throw Incompatible("The configured reasoning setting is not supported by the selected model.");
        }
        if (request.MaxTokens is not > 0) throw Unverifiable("A positive output reservation is required.");
        var allowance = await resolver.InputTokenAllowanceAsync(request.Provider, request.Model, request.MaxTokens.Value, ct);
        if (allowance is null) throw Unverifiable("The input allowance after reserving the configured output is unverifiable or incompatible.");
        var limit = Math.Min(hostLimit, allowance.Value);
        var current = EstimateTokens(request);
        if (current > limit)
            throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded,
                $"The complete serialized {stage} request is estimated at {current} tokens; its effective input limit is {limit} tokens. Dispatch did not start; cumulative budgets were not increased.",
                details: Details("request_input_allowance", limit, current));

        WorkflowRuntimeException Unverifiable(string reason) => new(ErrorCodes.LlmBudgetUnverifiable,
            reason + " Dispatch did not start; no settings were changed.", details: Details("model_capabilities", null, null));
        WorkflowRuntimeException Incompatible(string reason) => new("MODEL_REQUEST_INCOMPATIBLE",
            reason + " Dispatch did not start; no settings were changed.", details: Details("model_capabilities", null, null));
        JsonObject Details(string kind, int? limitValue, long? currentValue) => new()
        {
            ["classification"] = kind, ["limit_kind"] = kind, ["limit"] = limitValue, ["current"] = currentValue,
            ["measurement"] = "estimated_tokens", ["unit"] = "tokens", ["stage"] = stage,
            ["dispatch_status"] = "not_started"
        };
    }
}
