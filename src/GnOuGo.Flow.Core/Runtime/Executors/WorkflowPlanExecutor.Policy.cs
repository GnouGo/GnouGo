using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor : IStepExecutor
{
    private static void EnforcePolicy(WorkflowDocument doc, JsonObject policy)
    {
        var allowed = policy["allowed_step_types"] as JsonArray;
        var denied = policy["denied_step_types"] as JsonArray;
        var allowedSet = allowed?.Select(a => a?.GetValue<string>() ?? "").ToHashSet();
        var deniedSet = denied?.Select(a => a?.GetValue<string>() ?? "").ToHashSet();

        foreach (var step in doc.Workflows.Values.SelectMany(wf => EnumerateSteps(wf.Steps)))
        {
            if (allowedSet != null && !allowedSet.Contains(step.Type))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Step type '{step.Type}' not allowed by policy");
            if (deniedSet != null && deniedSet.Contains(step.Type))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Step type '{step.Type}' denied by policy");
        }

        var allowRemote = policy["allow_remote_workflow_refs"]?.GetValue<bool>() ?? false;
        if (!allowRemote)
        {
            foreach (var step in doc.Workflows.Values.SelectMany(wf => EnumerateSteps(wf.Steps)))
            {
                if (step.Type == "workflow.call" && step.Input is JsonObject inputObj)
                {
                    var refObj = inputObj["ref"] as JsonObject;
                    if (refObj?["kind"]?.GetValue<string>() == "url")
                        throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                            "Remote workflow references not allowed by policy");
                }
            }
        }
    }

    private static void EnforceLimits(WorkflowDocument doc, JsonObject limits)
    {
        var maxSteps = limits["max_steps_total"]?.GetValue<int>();
        if (maxSteps.HasValue)
        {
            var totalSteps = doc.Workflows.Values.Sum(wf => CountSteps(wf.Steps));
            if (totalSteps > maxSteps.Value)
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePolicy,
                    $"Total steps ({totalSteps}) exceeds limit ({maxSteps.Value})");
        }
    }

    private static int CountSteps(List<StepDef> steps)
    {
        var count = steps.Count;
        foreach (var step in steps)
        {
            if (step.Steps != null) count += CountSteps(step.Steps);
            if (step.Branches != null)
                count += step.Branches.Sum(b => CountSteps(b.Steps));
            if (step.Cases != null)
                count += step.Cases.Sum(c => CountSteps(c.Steps));
            if (step.Default != null) count += CountSteps(step.Default);
        }
        return count;
    }

    private static IEnumerable<StepDef> EnumerateSteps(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Steps != null)
            {
                foreach (var child in EnumerateSteps(step.Steps))
                    yield return child;
            }

            if (step.Branches != null)
            {
                foreach (var child in step.Branches.SelectMany(branch => EnumerateSteps(branch.Steps)))
                    yield return child;
            }

            if (step.Cases != null)
            {
                foreach (var child in step.Cases.SelectMany(@case => EnumerateSteps(@case.Steps)))
                    yield return child;
            }

            if (step.Default != null)
            {
                foreach (var child in EnumerateSteps(step.Default))
                    yield return child;
            }
        }
    }

    private static string BuildStepExceptionsDoc(StepExecutorRegistry registry, HashSet<string>? allowedTypes)
    {
        var catalogs = registry.GetStepExceptionCatalogs(allowedTypes)
            .OrderBy(c => c.StepType, StringComparer.Ordinal)
            .ToList();

        if (catalogs.Count == 0)
            return "No task-specific exception catalog is available.";

        var sb = new StringBuilder();
        sb.AppendLine("Common notes:");
        sb.AppendLine("- `INPUT_VALIDATION` usually means a required field is missing or has the wrong shape. It is usually non-retryable.");
        sb.AppendLine("- Only codes marked `retryable` should normally use `retry`.");

        var containerTypes = new[]
        {
            "sequence",
            "parallel",
            "loop.sequential",
            "loop.parallel",
            "switch",
            "workflow.call",
            "workflow.execute"
        };
        var visibleContainerTypes = containerTypes
            .Where(t => allowedTypes == null || allowedTypes.Contains(t))
            .ToList();
        if (visibleContainerTypes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Container child-error propagation:");
            sb.AppendLine("- These container steps can raise both their own documented errors and errors propagated from nested child steps.");
            foreach (var containerType in visibleContainerTypes)
            {
                var propagationNote = containerType switch
                {
                    "sequence" => "runs child steps sequentially, so any unhandled child failure can stop the container.",
                    "parallel" => "can fail because one branch throws an unhandled child error, in addition to its own parallel-limit checks.",
                    "loop.sequential" => "can fail because one iteration throws an unhandled child error, in addition to loop-limit checks. Supports items/over array iteration and while/times modes.",
                    "loop.parallel" => "can fail because one parallel iteration throws an unhandled child error, in addition to loop-limit checks.",
                    "switch" => "can fail because the selected case/default branch throws an unhandled child error.",
                    "workflow.call" => "can fail because the called sub-workflow throws an error, in addition to workflow reference/fetch/policy errors.",
                    "workflow.execute" => "can fail because the generated workflow throws an error, in addition to planned-YAML/entrypoint validation errors.",
                    _ => "can propagate child-step errors."
                };
                sb.AppendLine($"- `{containerType}`: {propagationNote}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Step-specific exceptions:");
        foreach (var catalog in catalogs)
        {
            sb.AppendLine();
            sb.AppendLine($"- {catalog.StepType}");
            foreach (var exception in catalog.Exceptions
                         .OrderBy(e => e.Code, StringComparer.Ordinal)
                         .ThenBy(e => e.Retryable))
            {
                sb.Append("  - ");
                sb.Append(exception.Code);
                sb.Append(exception.Retryable ? " (retryable)" : " (non-retryable)");
                sb.Append(": ");
                sb.AppendLine(exception.Description);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
