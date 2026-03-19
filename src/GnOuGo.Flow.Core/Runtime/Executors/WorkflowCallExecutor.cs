using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;

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

        if (kind == "local")
        {
            return await CallLocal(ctx, refObj, args, ct);
        }
        else if (kind == "url")
        {
            return await CallRemote(ctx, refObj, args, ct);
        }
        else
        {
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Unknown workflow.call kind: {kind}");
        }
    }

    private static async Task<JsonNode?> CallLocal(StepExecutionContext ctx, JsonObject refObj, JsonNode? args, CancellationToken ct)
    {
        var name = refObj["name"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Local workflow.call requires 'name'");

        // Check for cycles
        if (ctx.CallStack.Contains(name))
        {
            ctx.Engine.Logger.LogError("workflow.call: cycle detected, workflow '{WorkflowName}' already in call stack", name);
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Cycle detected: workflow '{name}' already in call stack");
        }

        // Look up compiled workflow from the step's document
        var compiledDoc = FindCompiledDocument(ctx);
        if (compiledDoc == null || !compiledDoc.Workflows.TryGetValue(name, out var subWorkflow))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Local workflow '{name}' not found");

        var newCallStack = new HashSet<string>(ctx.CallStack) { name };
        var result = new RunResult { Success = true };
        var subData = new JsonObject
        {
            ["inputs"] = args?.DeepClone() ?? new JsonObject(),
            ["steps"] = new JsonObject(),
            ["env"] = ctx.Data["env"]?.DeepClone() ?? new JsonObject()
        };

        await ctx.Engine.ExecuteStepsAsync(subWorkflow.Steps, subData, result, ctx.Limits, ctx.CallDepth + 1, newCallStack, ct);

        // Evaluate outputs
        JsonNode? outputs;
        if (subWorkflow.Outputs != null)
        {
            var outputObj = new JsonObject();
            foreach (var kv in subWorkflow.Outputs)
            {
                outputObj[kv.Key] = ctx.Engine.Interpolator.Interpolate(kv.Value, subData);
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
            ["workflow"] = name
        };
    }

    private static async Task<JsonNode?> CallRemote(StepExecutionContext ctx, JsonObject refObj, JsonNode? args, CancellationToken ct)
    {
        var url = refObj["url"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Remote workflow.call requires 'url'");

        var fetcher = ctx.Engine.WorkflowFetcher
            ?? throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchNetwork, "No workflow fetcher configured");

        // Policy check
        var policy = ctx.Engine.FetchPolicy;
        if (policy != null)
        {
            var uri = new Uri(url);
            if (policy.RequireHttps && uri.Scheme != "https")
                throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, "HTTPS required by policy");
            if (policy.AllowedHostnames.Count > 0 && !policy.AllowedHostnames.Contains(uri.Host))
                throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, $"Host '{uri.Host}' not in allow-list");
        }

        var integrity = refObj["integrity"]?.GetValue<string>();
        var yaml = await fetcher.FetchAsync(url, integrity, ct);

        // Parse and compile remote workflow
        var remoteDoc = WorkflowParser.Parse(yaml);
        var exportName = refObj["export"]?.GetValue<string>();

        WorkflowDef? targetWf = null;
        if (exportName != null)
        {
            if (remoteDoc.Exports == null || !remoteDoc.Exports.Contains(exportName))
                throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                    $"Workflow '{exportName}' is not exported from remote document");
            targetWf = remoteDoc.Workflows.GetValueOrDefault(exportName);
        }
        else if (remoteDoc.Exports?.Count == 1)
        {
            targetWf = remoteDoc.Workflows.GetValueOrDefault(remoteDoc.Exports[0]);
        }
        else if (remoteDoc.Entrypoint != null)
        {
            targetWf = remoteDoc.Workflows.GetValueOrDefault(remoteDoc.Entrypoint);
        }

        if (targetWf == null)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Could not resolve target workflow from remote document");

        // Compile and execute
        var compiler = new Compilation.WorkflowCompiler();
        var compiledDoc = compiler.Compile(remoteDoc);
        var compiledWf = compiledDoc.Workflows.Values.First();

        var subData = new JsonObject
        {
            ["inputs"] = args?.DeepClone() ?? new JsonObject(),
            ["steps"] = new JsonObject(),
            ["env"] = ctx.Data["env"]?.DeepClone() ?? new JsonObject()
        };

        var result = new RunResult { Success = true };
        await ctx.Engine.ExecuteStepsAsync(compiledWf.Steps, subData, result, ctx.Limits, ctx.CallDepth + 1, ctx.CallStack, ct);

        return new JsonObject
        {
            ["outputs"] = subData["steps"]?.DeepClone(),
            ["workflow"] = compiledWf.Name
        };
    }

    private static CompiledDocument? FindCompiledDocument(StepExecutionContext ctx)
    {
        return ctx.Engine.CompiledDocument;
    }
}






