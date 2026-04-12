using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Parallel loop: iterates over items in parallel.
/// </summary>
public sealed class LoopParallelExecutor : IStepExecutor
{
    public string StepType => "loop.parallel";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The step is missing nested `steps`, its input is not an object, or `items` is not an array."),
        new(ErrorCodes.LoopLimit, false, "The number of loop items exceeds the runtime loop limit.")
    };

    public string DslSnippet => """
        ### loop.parallel — Iterate items in parallel
        ```yaml
        - id: process
          type: loop.parallel
          input:
            items: "${data.inputs.my_list}"    # required — array expression
            max_concurrency: 2                  # optional
          item_var: item                        # variable name for current item (default: "item")
          index_var: idx                        # variable name for current index (default: "i")
          steps:
            - id: transform
              type: template.render
              input: { engine: mustache, template: "{{val}}", data: { val: "${data.item}" }, mode: text }
        ```
        Context: `data.<item_var>` (current item), `data.<index_var>` (current index).
        Output: `{ results: [...], count: N }`
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var subSteps = ctx.Step.Steps
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "loop.parallel requires 'steps'");

        var input = ctx.Engine.GetResolvedInput(ctx);
        var inputObj = input as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "loop.parallel input must be object");

        if (!inputObj.TryGetPropertyValue("items", out var itemsNode) || itemsNode is not JsonArray items)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "loop.parallel requires 'items' array");

        var itemVar = ctx.Step.Source.ItemVar ?? "item";
        var indexVar = ctx.Step.Source.IndexVar ?? "i";

        int maxConcurrency = 0;
        if (inputObj.TryGetPropertyValue("max_concurrency", out var mc) && mc != null)
            maxConcurrency = (int)ExpressionEvaluator.GetNumber(mc);

        if (items.Count > ctx.Limits.MaxLoopIterations)
            throw new WorkflowRuntimeException(ErrorCodes.LoopLimit,
                $"Loop items ({items.Count}) exceeds limit ({ctx.Limits.MaxLoopIterations})");

        var semaphore = maxConcurrency > 0 ? new SemaphoreSlim(maxConcurrency) : null;
        var tasks = new List<Task<(int index, JsonObject iterData)>>();

        for (int i = 0; i < items.Count; i++)
        {
            var index = i;
            var item = items[i];

            tasks.Add(Task.Run<(int, JsonObject)>(async () =>
            {
                if (semaphore != null) await semaphore.WaitAsync(ct);
                try
                {
                    var iterData = ctx.Data.DeepClone() as JsonObject ?? new JsonObject();
                    iterData[itemVar] = item?.DeepClone();
                    iterData[indexVar] = JsonValue.Create(index);
                    iterData["_loop"] = new JsonObject { ["index"] = JsonValue.Create(index), ["item"] = item?.DeepClone() };
                    iterData["loop"] = new JsonObject { ["index"] = JsonValue.Create(index), ["item"] = item?.DeepClone() };

                    var iterResult = new RunResult { Success = true };
                    await ctx.Engine.ExecuteStepsAsync(subSteps, iterData, iterResult, ctx.Limits, ctx.CallDepth, ctx.CallStack, ct, ctx.TelemetrySpan);
                    return (index, iterData);
                }
                finally
                {
                    semaphore?.Release();
                }
            }, ct));
        }


        var results = await Task.WhenAll(tasks);

        var output = new JsonArray();
        foreach (var (_, iterData) in results.OrderBy(r => r.index))
        {
            var stepData = iterData["steps"]?.DeepClone() as JsonObject ?? new JsonObject();
            // Clean up temp keys
            var keysToRemove = stepData.Select(kv => kv.Key).Where(k => k.StartsWith("__") && k.EndsWith("__")).ToList();
            foreach (var key in keysToRemove) stepData.Remove(key);
            output.Add((JsonNode)stepData);
        }

        return new JsonObject { ["results"] = output, ["count"] = items.Count };
    }
}
