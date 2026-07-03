using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Sequential loop: iterates over an <c>items</c> (or <c>over</c>) array, or loops <c>times</c>/<c>while</c> times.
/// </summary>
public sealed class LoopSequentialExecutor : IStepExecutor
{
    public string StepType => "loop.sequential";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The step is missing its nested `steps` collection or has invalid loop input values."),
        new(ErrorCodes.LoopLimit, false, "The loop exceeded `max_times` or the runtime maximum loop iteration limit.")
    };

    public string DslSnippet => """
        ### loop.sequential — Sequential loop over items or with while/times
        ```yaml
        # Iterate over a list sequentially:
        - id: process
          type: loop.sequential
          input:
            items: "${data.inputs.my_list}"    # required — array expression (alias: 'over')
          item_var: item                        # variable name for current item (default: "item")
          index_var: idx                        # variable name for current index (default: "i")
          steps:
            - id: transform
              type: set
              input: { value: "${data.item}" }

        # Fixed iteration count:
        - id: loop
          type: loop.sequential
          input:
            times: 5
            # or: while: "${data._loop.index < 10}"
          steps:
            - id: iter
              type: template.render
              input: { engine: mustache, template: "Iteration {{idx}}", data: { idx: "${data._loop.index}" }, mode: text }
        ```
        Context: `data.<item_var>` (current item), `data.<index_var>` (current index), `data._loop.index`, `data._loop.item` (when iterating items).
        Output: `{ results: [...], count: N }`
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var subSteps = ctx.Step.Steps
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "loop.sequential requires 'steps'");

        var input = ctx.Engine.GetResolvedInput(ctx);
        var inputObj = input as JsonObject;

        int maxTimes = ctx.Limits.MaxLoopIterations;
        if (inputObj?.TryGetPropertyValue("max_times", out var mt) == true && mt != null)
            maxTimes = (int)ExpressionEvaluator.GetNumber(mt);

        // Resolve items array: support both 'items' and 'over' (alias)
        JsonArray? items = null;
        bool itemsKeyPresent = false;
        JsonNode? rawItemsValue = null;
        if (inputObj?.TryGetPropertyValue("items", out var itemsNode) == true && itemsNode != null)
        {
            itemsKeyPresent = true;
            rawItemsValue = itemsNode;
            if (itemsNode is JsonArray itemsArr)
                items = itemsArr;
        }
        else if (inputObj?.TryGetPropertyValue("over", out var overNode) == true && overNode != null)
        {
            itemsKeyPresent = true;
            rawItemsValue = overNode;
            if (overNode is JsonArray overArr)
                items = overArr;
        }

        int? times = null;
        if (inputObj?.TryGetPropertyValue("times", out var t) == true && t != null)
            times = (int)ExpressionEvaluator.GetNumber(t);

        // Validate: items and times are mutually exclusive
        if (items != null && times != null)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "loop.sequential: 'items'/'over' and 'times' are mutually exclusive");

        // Validate: if 'items'/'over' key was specified but didn't resolve to an array, fail early
        if (itemsKeyPresent && items == null)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "loop.sequential: 'items'/'over' must resolve to an array. "
                + $"Got: {rawItemsValue?.GetValueKind().ToString() ?? "null"}. "
                + "Check that the expression returns a JSON array.");

        // ── items-based iteration ──────────────────────────────────────
        if (items != null)
        {
            var itemVar = ctx.Step.Source.ItemVar ?? "item";
            var indexVar = ctx.Step.Source.IndexVar ?? "i";

            if (items.Count > maxTimes)
                throw new WorkflowRuntimeException(ErrorCodes.LoopLimit,
                    $"Loop items ({items.Count}) exceeds limit ({maxTimes})");

            var iterations = new JsonArray();

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var currentItem = items[i]?.DeepClone();
                ctx.Data[itemVar] = currentItem?.DeepClone();
                ctx.Data[indexVar] = JsonValue.Create(i);
                ctx.Data["_loop"] = new JsonObject { ["index"] = JsonValue.Create(i), ["item"] = currentItem?.DeepClone() };
                ctx.Data["loop"] = new JsonObject { ["index"] = JsonValue.Create(i), ["item"] = currentItem?.DeepClone() };

                // Evaluate while condition if present (combined items + while)
                if (inputObj?.TryGetPropertyValue("while", out var whileExpr) == true && whileExpr != null)
                {
                    var whileStr = ExpressionEvaluator.GetString(whileExpr);
                    var condResult = ctx.Interpolator.Interpolate(whileStr, ctx.Data);
                    if (!ExpressionEvaluator.GetBool(condResult))
                        break;
                }

                var result = new RunResult { Success = true };
                await ctx.Engine.ExecuteStepsAsync(
                    subSteps,
                    ctx.Data,
                    result,
                    ctx.Limits,
                    ctx.CallDepth,
                    ctx.CallStack,
                    ctx.EffectiveExecutionScope,
                    ct,
                    ctx.TelemetrySpan);

                iterations.Add(ctx.Data["steps"]?.DeepClone() ?? new JsonObject());
            }

            // Clean up loop-scoped variables
            ctx.Data.Remove(itemVar);
            ctx.Data.Remove(indexVar);
            ctx.Data.Remove("_loop");
            ctx.Data.Remove("loop");

            return new JsonObject { ["results"] = iterations, ["count"] = iterations.Count };
        }

        // ── times / while iteration ────────────────────────────────────
        bool hasWhile = inputObj?.ContainsKey("while") == true;

        // Guard: at least one termination condition must be present
        if (!times.HasValue && !hasWhile)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "loop.sequential requires at least one of: 'items', 'over', 'times', or 'while'");

        var whileIterations = new JsonArray();
        int iteration = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (times.HasValue && iteration >= times.Value)
                break;

            if (iteration >= maxTimes)
                throw new WorkflowRuntimeException(ErrorCodes.LoopLimit,
                    $"Loop iteration limit reached ({maxTimes})");

            ctx.Data["_loop"] = new JsonObject { ["index"] = iteration };
            ctx.Data["loop"] = new JsonObject { ["index"] = iteration };

            // Evaluate while condition
            if (inputObj?.TryGetPropertyValue("while", out var whileExpr2) == true && whileExpr2 != null)
            {
                var whileStr = ExpressionEvaluator.GetString(whileExpr2);
                var condResult = ctx.Interpolator.Interpolate(whileStr, ctx.Data);
                if (!ExpressionEvaluator.GetBool(condResult))
                    break;
            }

            // Execute iteration
            var result2 = new RunResult { Success = true };
            await ctx.Engine.ExecuteStepsAsync(
                subSteps,
                ctx.Data,
                result2,
                ctx.Limits,
                ctx.CallDepth,
                ctx.CallStack,
                ctx.EffectiveExecutionScope,
                ct,
                ctx.TelemetrySpan);

            whileIterations.Add(ctx.Data["steps"]?.DeepClone() ?? new JsonObject());
            iteration++;
        }

        return new JsonObject { ["results"] = whileIterations, ["count"] = iteration };
    }
}
