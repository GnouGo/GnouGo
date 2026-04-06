using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Executes steps sequentially.
/// </summary>
public sealed class SequenceExecutor : IStepExecutor
{
    public string StepType => "sequence";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The step is missing its nested `steps` collection.")
    };

    public string DslSnippet => """
        ### sequence — Execute sub-steps sequentially
        ```yaml
        - id: setup
          type: sequence
          steps:
            - id: step_a
              type: template.render
              input: { engine: mustache, template: "Hello", mode: text }
            - id: step_b
              type: template.render
              input: { engine: mustache, template: "World", mode: text }
        ```
        Output: object with each child step's output keyed by step id.
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var subSteps = ctx.Step.Steps
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "sequence step requires 'steps'");

        var result = new RunResult { Success = true };
        await ctx.Engine.ExecuteStepsAsync(subSteps, ctx.Data, result, ctx.Limits, ctx.CallDepth, ctx.CallStack, ct, ctx.TelemetrySpan);

        // Return the collected step outputs
        var outputs = new JsonObject();
        foreach (var sr in result.StepResults.Where(s => s.Status == StepStatus.Succeeded))
        {
            outputs[sr.StepId] = sr.Output?.DeepClone();
        }
        return outputs;
    }
}
