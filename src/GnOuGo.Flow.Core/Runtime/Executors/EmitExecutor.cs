using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Emits a thinking / progress / info / response message to the telemetry stream.
/// Allows workflows to push real-time feedback to the user interface.
///
/// Input:
///   - message (string, required) : The message to emit.
///   - level   (string, optional) : One of "thinking", "info", "progress", "response". Default: "thinking".
///
/// Output:
///   { message: "...", level: "..." }
/// </summary>
public sealed class EmitExecutor : IStepExecutor
{
    public string StepType => "emit";

    private static readonly HashSet<string> ValidLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "thinking", "info", "progress", "response"
    };

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input object is malformed or the 'message' field is missing.")
    };

    public string DslSnippet => """
        ### emit — Emit a thinking / progress message
        Sends a real-time message to the user interface. Useful for showing progress,
        thinking steps, or partial responses during long-running workflows.
        Levels: `thinking` (default, subtle animated), `info` (blue), `progress` (green), `response` (highlighted, monospace).
        ```yaml
        - id: thinking_step
          type: emit
          input:
            message: "Analyzing the data..."
            level: thinking

        - id: progress_step
          type: emit
          input:
            message: "Processed 3 of 5 items"
            level: progress

        - id: early_response
          type: emit
          input:
            message: "Here is a preliminary answer: ..."
            level: response
        ```
        Output: `{ message: "...", level: "..." }`
        """;

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "emit input must be object");

        var message = input["message"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "emit requires 'message'");

        var level = input["level"]?.GetValue<string>()?.ToLowerInvariant() ?? "thinking";
        if (!ValidLevels.Contains(level))
            level = "thinking";

        // Fire a telemetry event so StreamingWorkflowTelemetry can push it to the browser
        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", message),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", level)
        });

        var result = new JsonObject
        {
            ["message"] = message,
            ["level"] = level
        };

        return Task.FromResult<JsonNode?>(result);
    }
}

