using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Calls a local or remote sub-workflow.
/// </summary>
public sealed class WorkflowCallExecutor : IStepExecutor
{
    public string StepType => "workflow.call";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The workflow reference is malformed, the target name/url is missing, or the target workflow cannot be resolved."),
        new(ErrorCodes.WorkflowCycleDetected, false, "The call depth limit was exceeded or a local workflow cycle was detected."),
        new(ErrorCodes.WorkflowFetchPolicy, false, "A remote workflow violates HTTPS/host/export policy constraints."),
        new(ErrorCodes.WorkflowFetchNetwork, false, "A remote workflow fetcher is missing or the remote workflow could not be fetched."),
        new(ErrorCodes.WorkflowFetchIntegrity, false, "The remote workflow integrity check failed.")
    };

    public string DslSnippet => """
        ### workflow.call — Call a sub-workflow (local or remote)
        ```yaml
        - id: sub_task
          type: workflow.call
          input:
            ref:
              kind: local                       # "local" (same document) or "url" (remote)
              name: helper_workflow              # workflow name (local) or URL (remote)
            args:                               # input arguments for the sub-workflow
              key: "${data.inputs.value}"
        ```
        Output: `{ outputs: { ... }, workflow: "name", run: { steps_executed, success } }`
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.call input must be object");

        var refObj = input["ref"] as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.call requires 'ref'");

        var kind = refObj["kind"]?.GetValue<string>() ?? "local";
        var args = input["args"] ?? new JsonObject();

        // Check call depth
        if (ctx.CallDepth >= ctx.Limits.MaxCallDepth)
        {
            ctx.Engine.Logger.LogError("workflow.call: max call depth ({MaxCallDepth}) exceeded", ctx.Limits.MaxCallDepth);
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Max call depth ({ctx.Limits.MaxCallDepth}) exceeded");
        }

        var resolution = await ctx.Engine.WorkflowCallResolver.ResolveAsync(new WorkflowCallResolutionContext
        {
            Engine = ctx.Engine,
            Ref = refObj,
            Kind = kind,
            CallDepth = ctx.CallDepth,
            CallStack = ctx.CallStack
        }, ct);

        if (!string.IsNullOrWhiteSpace(resolution.CallStackKey) && ctx.CallStack.Contains(resolution.CallStackKey))
        {
            ctx.Engine.Logger.LogError("workflow.call: cycle detected, workflow call '{WorkflowCallKey}' already in call stack", resolution.CallStackKey);
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Cycle detected: workflow '{resolution.WorkflowName}' already in call stack");
        }

        return await ExecuteResolvedWorkflow(ctx, resolution, args, ct);
    }

    private static async Task<JsonNode?> ExecuteResolvedWorkflow(
        StepExecutionContext ctx,
        WorkflowCallResolution resolution,
        JsonNode? args,
        CancellationToken ct)
    {
        var subWorkflow = resolution.Workflow;
        var newCallStack = new HashSet<string>(ctx.CallStack);
        if (!string.IsNullOrWhiteSpace(resolution.CallStackKey))
            newCallStack.Add(resolution.CallStackKey);

        var resolvedArgs = WorkflowInputDefaults.Apply(subWorkflow.Source, args);
        var inputErrors = InputTypeValidator.Validate(subWorkflow.Source, resolvedArgs);
        if (inputErrors.Count > 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"Input validation failed for called workflow '{resolution.WorkflowName}': {string.Join("; ", inputErrors)}",
                details: new JsonObject
                {
                    ["workflow"] = resolution.WorkflowName,
                    ["validation_errors"] = new JsonArray(inputErrors.Select(static error => (JsonNode)JsonValue.Create(error)!).ToArray())
                });
        }

        var result = new RunResult { Success = true };
        var subData = new JsonObject
        {
            ["inputs"] = resolvedArgs,
            ["steps"] = new JsonObject(),
            ["env"] = ctx.Data["env"]?.DeepClone() ?? new JsonObject()
        };

        var previousDocument = ctx.Engine.ReplaceCompiledDocumentForWorkflowCall(subWorkflow.Document);
        try
        {
            await ctx.Engine.ExecuteStepsAsync(subWorkflow.Steps, subData, result, ctx.Limits, ctx.CallDepth + 1, newCallStack, ct, ctx.TelemetrySpan);
        }
        finally
        {
            ctx.Engine.ReplaceCompiledDocumentForWorkflowCall(previousDocument);
        }

        // Evaluate outputs
        JsonNode? outputs;
        if (subWorkflow.Outputs != null)
        {
            var outputObj = new JsonObject();
            foreach (var kv in subWorkflow.Outputs)
            {
                outputObj[kv.Key] = ctx.Engine.EvaluateOutputDef(kv.Value, subData);
            }
            outputs = outputObj;
        }
        else
        {
            outputs = subData["steps"]?.DeepClone();
        }

        return new JsonObject
        {
            ["outputs"] = outputs,
            ["workflow"] = resolution.WorkflowName
        };
    }
}





