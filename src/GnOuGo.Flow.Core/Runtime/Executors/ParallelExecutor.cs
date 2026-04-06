using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Executes branches in parallel.
/// </summary>
public sealed class ParallelExecutor : IStepExecutor
{
    public string StepType => "parallel";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The step is missing its `branches` collection."),
        new(ErrorCodes.ParallelLimit, false, "The workflow exceeds the configured maximum number of parallel branches.")
    };

    public string DslSnippet => """
        ### parallel — Execute branches in parallel
        ```yaml
        - id: par
          type: parallel
          input: { max_concurrency: 3 }    # optional
          branches:
            - steps:
                - id: b1
                  type: template.render
                  input: { engine: mustache, template: "Branch1", mode: text }
            - steps:
                - id: b2
                  type: template.render
                  input: { engine: mustache, template: "Branch2", mode: text }
        ```
        Output: object with each branch index containing its step outputs.
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var branches = ctx.Step.Branches
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "parallel step requires 'branches'");

        var input = ctx.Engine.GetResolvedInput(ctx);
        var maxConcurrency = 0;
        if (input is JsonObject inputObj && inputObj.TryGetPropertyValue("max_concurrency", out var mc) && mc != null)
            maxConcurrency = (int)ExpressionEvaluator.GetNumber(mc);

        if (branches.Count > ctx.Limits.MaxParallelBranches)
            throw new WorkflowRuntimeException(ErrorCodes.ParallelLimit,
                $"Parallel branches ({branches.Count}) exceeds limit ({ctx.Limits.MaxParallelBranches})");

        // Snapshot context for each branch
        var tasks = new List<Task<(int index, JsonObject branchData)>>();
        var semaphore = maxConcurrency > 0 ? new SemaphoreSlim(maxConcurrency) : null;

        for (int i = 0; i < branches.Count; i++)
        {
            var branchIndex = i;
            var branch = branches[i];
            var branchData = ctx.Data.DeepClone() as JsonObject ?? new JsonObject();

            tasks.Add(Task.Run<(int, JsonObject)>(async () =>
            {
                if (semaphore != null) await semaphore.WaitAsync(ct);
                try
                {
                    var result = new RunResult { Success = true };
                    await ctx.Engine.ExecuteStepsAsync(branch, branchData, result, ctx.Limits, ctx.CallDepth, ctx.CallStack, ct, ctx.TelemetrySpan);
                    return (branchIndex, branchData);
                }
                finally
                {
                    semaphore?.Release();
                }
            }, ct));
        }

        var results = await Task.WhenAll(tasks);

        // Aggregate results
        var output = new JsonObject();
        var branchResults = new JsonArray();
        foreach (var (_, branchData) in results.OrderBy(r => r.index))
        {
            branchResults.Add((JsonNode)(branchData["steps"]?.DeepClone() ?? new JsonObject()));
        }
        output["branches"] = branchResults;
        return output;
    }
}
