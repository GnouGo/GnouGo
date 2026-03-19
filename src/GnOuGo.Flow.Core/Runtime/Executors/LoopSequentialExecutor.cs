using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Sequential loop: while/times based.
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
        ### loop.sequential — Loop with while/times
        ```yaml
        - id: loop
          type: loop.sequential
          input:
            times: 5                # fixed iteration count
            # or: while: "${data._loop.index < 10}"
          steps:
            - id: iter
              type: template.render
              input: { engine: mustache, template: "Iteration {{idx}}", data: { idx: "${data._loop.index}" }, mode: text }
        ```
        Context: `data._loop.index` (current 0-based iteration index, available from 0 during the first `while` evaluation).
        Output: `{ iterations: [...], count: N }`
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var subSteps = ctx.Step.Steps
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "loop.sequential requires 'steps'");

        var input = ctx.Engine.GetResolvedInput(ctx);
        var inputObj = input as JsonObject;

        int? times = null;
        int maxTimes = ctx.Limits.MaxLoopIterations;

        if (inputObj != null)
        {
            if (inputObj.TryGetPropertyValue("times", out var t) && t != null)
                times = (int)ExpressionEvaluator.GetNumber(t);
            if (inputObj.TryGetPropertyValue("max_times", out var mt) && mt != null)
                maxTimes = (int)ExpressionEvaluator.GetNumber(mt);
        }

        var iterations = new JsonArray();
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
            if (inputObj?.TryGetPropertyValue("while", out var whileExpr) == true && whileExpr != null)
            {
                var whileStr = ExpressionEvaluator.GetString(whileExpr);
                var condResult = ctx.Engine.Interpolator.Interpolate(whileStr, ctx.Data);
                if (!ExpressionEvaluator.GetBool(condResult))
                    break;
            }

            // Execute iteration
            var result = new RunResult { Success = true };
            await ctx.Engine.ExecuteStepsAsync(subSteps, ctx.Data, result, ctx.Limits, ctx.CallDepth, ctx.CallStack, ct);

            iterations.Add(ctx.Data["steps"]?.DeepClone() ?? new JsonObject());
            iteration++;
        }

        return new JsonObject { ["iterations"] = iterations, ["count"] = iteration };
    }
}
