using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class DynamicMappingExecutor
{
    private static async Task PackCollectionRequestAsync(StepExecutionContext ctx, ILLMClient client, LLMRequest request,
        JsonObject sources, JsonArray items, string input, JsonObject target, JsonObject itemTarget, JsonNode objective,
        string? previous, string? failure, int? failedIndex, CancellationToken ct, IReadOnlyList<int>? adaptiveIndices = null)
    {
        var allowance = (ctx.Engine.LLMCapabilities ?? client as ILLMCapabilityResolver) is { } resolver
            ? await resolver.InputTokenAllowanceAsync(request.Provider, request.Model, request.MaxTokens!.Value, ct) : null;
        if (allowance is null)
            throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable, "No verified input allowance for mapping generation.");
        var limit = Math.Min(allowance.Value, ctx.Limits.MaxMappingInputTokens ?? 12_000);
        var examples = new JsonArray();
        var payload = new JsonObject
        {
            ["objective"] = objective.DeepClone(),
            ["context"] = new JsonObject(sources.Where(p => p.Key != input).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()))),
            ["item_target"] = itemTarget.DeepClone(), ["complete_target"] = target.DeepClone(),
            ["source_count"] = items.Count, ["omitted_count"] = items.Count, ["examples"] = examples,
            ["previous_script"] = adaptiveIndices is null ? previous : null, ["failure"] = failure, ["failing_index"] = failedIndex
        };
        if (adaptiveIndices is not null) payload["assigned_count"] = adaptiveIndices.Count;
        var collectionInstruction = "\nIndependent-item extraction. Return the result for ONE item, against item_target. " +
            "Use item for the current original element (for example item.records), and context for the other approved read-only inputs. " +
            "Do not wrap item in the business input name. The host binds these variables exactly as examples[].item and context below. " +
            "Examples are incomplete observations of the collection, never evidence that omitted items are empty or absent. " +
            (adaptiveIndices is null
                ? "The host applies this same expression to EVERY original item, preserving order and nesting, with no filtering or aggregation.\n"
                : "The host validates this expression against EVERY assigned unresolved item. Previously validated results stay unchanged. The complete result retains every original item in order and nesting, with no filtering or aggregation.\n");
        bool Fits()
        {
            request.Prompt = Instructions + collectionInstruction + payload.ToJsonString();
            // UTF-8 bytes are a conservative bound for the supported byte-level
            // tokenizers. Reserve framing in addition to the complete Flow request.
            return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 4096 <= limit;
        }
        if (!Fits()) throw JintSandbox.Unsatisfied("The approved mapping context and target exceed the safe request allowance.");
        var shapes = new HashSet<string>(StringComparer.Ordinal);
        var required = new List<int>();
        if (failedIndex is >= 0 && failedIndex < items.Count) required.Add(failedIndex.Value);
        foreach (var i in adaptiveIndices ?? Enumerable.Range(0, items.Count).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (shapes.Add(Hash(Shape(items[i])))) required.Add(i);
        }
        var selected = new HashSet<int>();
        void Add(int index, bool mandatory)
        {
            if (!selected.Add(index)) return;
            examples.Add((JsonNode)new JsonObject { ["index"] = index, ["item"] = items[index]?.DeepClone() });
            payload["omitted_count"] = items.Count - examples.Count;
            if (Fits()) return;
            examples.RemoveAt(examples.Count - 1); selected.Remove(index);
            payload["omitted_count"] = items.Count - examples.Count;
            if (mandatory) throw new WorkflowRuntimeException("CONTRACT_UNSATISFIED",
                "A required complete mapping example exceeds the safe request allowance.", details: new JsonObject { ["source_index"] = index });
        }
        if (adaptiveIndices is not null && failedIndex is { } requiredIndex) Add(requiredIndex, true);
        // Invalid syntax/signatures have no failing data item. Still reserve one
        // complete fitting example before adding optional previous-program text.
        if (adaptiveIndices is not null && failedIndex is null && previous is not null)
            foreach (var index in adaptiveIndices)
            { Add(index, false); if (examples.Count != 0) break; }
        if (adaptiveIndices is not null && previous is not null)
        {
            payload["previous_script"] = previous;
            if (!Fits()) payload["previous_script"] = null;
        }
        foreach (var index in required) Add(index, adaptiveIndices is null || index == failedIndex);
        Add(adaptiveIndices?[0] ?? 0, false); Add(adaptiveIndices?[^1] ?? items.Count - 1, false);
        // An oversized representative does not make its entire shape unusable.
        if (adaptiveIndices is not null && examples.Count == 0)
            foreach (var index in adaptiveIndices)
            { Add(index, false); if (examples.Count != 0) break; }
        if (examples.Count == 0) throw JintSandbox.Unsatisfied("No complete initial mapping example fits the safe request allowance.");
        _ = Fits();
        ctx.SetTelemetryAttribute("gnougo.mapping.sample_items", examples.Count);
        ctx.SetTelemetryAttribute("gnougo.mapping.omitted_items", items.Count - examples.Count);
        ctx.SetTelemetryAttribute("gnougo.mapping.request_bytes", Encoding.UTF8.GetByteCount(request.Prompt));
    }
}
