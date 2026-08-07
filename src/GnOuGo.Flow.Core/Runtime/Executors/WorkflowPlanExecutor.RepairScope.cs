using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private static void ValidateSurgicalRepairScope(
        JsonObject? scope,
        WorkflowDocument candidate)
    {
        if (scope is null)
            return;

        var existingYaml = TryGetString(scope["existing_yaml"]);
        var stepId = TryGetString(scope["step_id"]);
        var requestedWorkflow = TryGetString(scope["workflow"]);
        if (string.IsNullOrWhiteSpace(existingYaml) || string.IsNullOrWhiteSpace(stepId))
            return;

        WorkflowDocument original;
        try
        {
            original = WorkflowParser.Parse(existingYaml);
        }
        catch (Exception ex)
        {
            throw RepairScopeViolation(
                "repair.existing_yaml",
                $"The original workflow could not be parsed for surgical repair validation: {ex.Message}");
        }

        var targetWorkflow = ResolveRepairTargetWorkflow(original, requestedWorkflow, stepId);
        var originalTargetStep = FindStep(original.Workflows[targetWorkflow], stepId)
                                 ?? throw RepairScopeViolation(
                                     "repair.scope.step_id",
                                     $"Failing step '{stepId}' was not found in the original workflow.");
        var candidateTargetStep = candidate.Workflows.TryGetValue(targetWorkflow, out var candidateTargetWorkflow)
            ? FindStep(candidateTargetWorkflow, stepId)
            : null;
        var repeatedMcpRepair = candidateTargetStep is null
            ? null
            : CreateRepeatedMcpRepairPattern(originalTargetStep, candidateTargetStep);
        EnsureEquivalent(original.Version, candidate.Version, "version");
        EnsureEquivalent(original.Name, candidate.Name, "name");
        EnsureEquivalent(original.Meta, candidate.Meta, "meta");
        EnsureEquivalent(original.Skill, candidate.Skill, "skill");
        EnsureEquivalent(original.Functions, candidate.Functions, "functions");
        EnsureEquivalent(original.Exports, candidate.Exports, "exports");
        EnsureEquivalent(original.Entrypoint, candidate.Entrypoint, "entrypoint");

        var originalNames = original.Workflows.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var candidateNames = candidate.Workflows.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!originalNames.SequenceEqual(candidateNames, StringComparer.Ordinal))
        {
            throw RepairScopeViolation(
                "workflows",
                "A surgical repair must preserve the complete set of workflows; local sub-workflows cannot be added, removed, or renamed.");
        }

        var changedTargetRegion = false;
        foreach (var workflowName in originalNames)
        {
            var originalWorkflow = original.Workflows[workflowName];
            var candidateWorkflow = candidate.Workflows[workflowName];
            var isTargetWorkflow = string.Equals(workflowName, targetWorkflow, StringComparison.Ordinal);
            var path = $"workflows.{workflowName}";

            EnsureEquivalent(originalWorkflow.Inputs, candidateWorkflow.Inputs, path + ".inputs");
            EnsureEquivalent(originalWorkflow.Skill, candidateWorkflow.Skill, path + ".skill");
            EnsureEquivalent(originalWorkflow.Functions, candidateWorkflow.Functions, path + ".functions");
            ValidateWorkflowOutputs(
                originalWorkflow.Outputs,
                candidateWorkflow.Outputs,
                path + ".outputs",
                isTargetWorkflow ? stepId : null,
                ref changedTargetRegion);
            ValidateRepairStepList(
                originalWorkflow.Steps,
                candidateWorkflow.Steps,
                path + ".steps",
                isTargetWorkflow ? stepId : null,
                isTargetWorkflow ? repeatedMcpRepair : null,
                ref changedTargetRegion);
            ValidateRepairStepList(
                originalWorkflow.Finally,
                candidateWorkflow.Finally,
                path + ".finally",
                isTargetWorkflow ? stepId : null,
                isTargetWorkflow ? repeatedMcpRepair : null,
                ref changedTargetRegion);
        }

        if (!changedTargetRegion)
        {
            throw RepairScopeViolation(
                $"workflows.{targetWorkflow}.steps.{stepId}",
                $"The proposed repair did not change failing step '{stepId}' or one of its existing direct consumers.");
        }
    }

    private static string ResolveRepairTargetWorkflow(
        WorkflowDocument original,
        string? requestedWorkflow,
        string stepId)
    {
        if (!string.IsNullOrWhiteSpace(requestedWorkflow)
            && original.Workflows.TryGetValue(requestedWorkflow, out var requested)
            && ContainsStep(requested.Steps, stepId))
        {
            return requestedWorkflow;
        }

        var matches = original.Workflows
            .Where(pair => ContainsStep(pair.Value.Steps, stepId) || ContainsStep(pair.Value.Finally, stepId))
            .Select(static pair => pair.Key)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw RepairScopeViolation(
                "repair.scope.step_id",
                $"Failing step '{stepId}' was not found in the original workflow."),
            _ => throw RepairScopeViolation(
                "repair.scope.step_id",
                $"Failing step '{stepId}' exists in several workflows; an exact workflow name is required for a safe repair.")
        };
    }

    private static bool ContainsStep(IReadOnlyList<StepDef> steps, string stepId)
    {
        foreach (var step in steps)
        {
            if (string.Equals(step.Id, stepId, StringComparison.Ordinal))
                return true;
            if (step.Steps is not null && ContainsStep(step.Steps, stepId))
                return true;
            if (step.Default is not null && ContainsStep(step.Default, stepId))
                return true;
            if (step.Branches?.Any(branch => ContainsStep(branch.Steps, stepId)) == true)
                return true;
            if (step.Cases?.Any(item => ContainsStep(item.Steps, stepId)) == true)
                return true;
        }

        return false;
    }

    private static StepDef? FindStep(WorkflowDef workflow, string stepId)
        => FindStep(workflow.Steps, stepId) ?? FindStep(workflow.Finally, stepId);

    private static StepDef? FindStep(IReadOnlyList<StepDef> steps, string stepId)
    {
        foreach (var step in steps)
        {
            if (string.Equals(step.Id, stepId, StringComparison.Ordinal))
                return step;

            var nested = FindStep(step.Steps ?? [], stepId)
                         ?? FindStep(step.Default ?? [], stepId);
            if (nested is not null)
                return nested;

            if (step.Branches is not null)
            {
                foreach (var branch in step.Branches)
                {
                    nested = FindStep(branch.Steps, stepId);
                    if (nested is not null)
                        return nested;
                }
            }

            if (step.Cases is null)
                continue;
            foreach (var item in step.Cases)
            {
                nested = FindStep(item.Steps, stepId);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static void ValidateWorkflowOutputs(
        IReadOnlyDictionary<string, OutputDef>? original,
        IReadOnlyDictionary<string, OutputDef>? candidate,
        string path,
        string? targetStepId,
        ref bool changedTargetRegion)
    {
        if (original is null || candidate is null)
        {
            EnsureEquivalent(original, candidate, path);
            return;
        }

        if (!original.Keys.OrderBy(static key => key, StringComparer.Ordinal)
                .SequenceEqual(candidate.Keys.OrderBy(static key => key, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw RepairScopeViolation(path, "Workflow output names must be preserved by a surgical repair.");
        }

        foreach (var (name, originalOutput) in original)
        {
            var candidateOutput = candidate[name];
            if (EquivalentOutputContractCore(originalOutput, candidateOutput)
                && string.Equals(originalOutput.Expr, candidateOutput.Expr, StringComparison.Ordinal))
                continue;

            var mayAdjustExpression = !string.IsNullOrWhiteSpace(targetStepId)
                                      && ReferencesStep(originalOutput.Expr, targetStepId);
            if (!mayAdjustExpression || !EquivalentOutputContract(originalOutput, candidateOutput))
            {
                throw RepairScopeViolation(
                    $"{path}.{name}",
                    "Only the expression of an existing output that directly consumes the failing step may change; its public contract must remain intact.");
            }

            changedTargetRegion = true;
        }
    }

    private static RepeatedMcpRepairPattern? CreateRepeatedMcpRepairPattern(
        StepDef originalTarget,
        StepDef candidateTarget)
    {
        if (!TryReadMcpCallIdentity(originalTarget, out var beforeIdentity)
            || !TryReadMcpCallIdentity(candidateTarget, out var afterIdentity))
        {
            return null;
        }

        var beforeShell = BuildStepShell(originalTarget);
        var afterShell = BuildStepShell(candidateTarget);
        return JsonNode.DeepEquals(beforeShell, afterShell)
            ? null
            : new RepeatedMcpRepairPattern(beforeIdentity, afterIdentity, beforeShell, afterShell);
    }

    private static bool IsExactRepeatedMcpRepair(
        StepDef before,
        StepDef after,
        RepeatedMcpRepairPattern? pattern)
    {
        if (pattern is null
            || !TryReadMcpCallIdentity(before, out var beforeIdentity)
            || !TryReadMcpCallIdentity(after, out var afterIdentity)
            || beforeIdentity != pattern.BeforeIdentity
            || afterIdentity != pattern.AfterIdentity)
        {
            return false;
        }

        return ReplaysExactPatch(
            pattern.BeforeShell,
            pattern.AfterShell,
            BuildStepShell(before),
            BuildStepShell(after));
    }

    private static bool TryReadMcpCallIdentity(StepDef step, out McpCallIdentity identity)
    {
        identity = default;
        if (!string.Equals(step.Type, "mcp.call", StringComparison.OrdinalIgnoreCase)
            || step.Input is not JsonObject input)
        {
            return false;
        }

        var server = TryGetString(input["server"]);
        var method = TryGetString(input["method"]);
        var kind = TryGetString(input["kind"]) ?? "tool";
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(method))
            return false;

        identity = new McpCallIdentity(server, kind, method);
        return true;
    }

    private static bool ReplaysExactPatch(
        JsonNode? patternBefore,
        JsonNode? patternAfter,
        JsonNode? siblingBefore,
        JsonNode? siblingAfter)
        => ReplaysExactPatch(
            patternBeforeExists: true,
            patternBefore,
            patternAfterExists: true,
            patternAfter,
            siblingBeforeExists: true,
            siblingBefore,
            siblingAfterExists: true,
            siblingAfter);

    private static bool ReplaysExactPatch(
        bool patternBeforeExists,
        JsonNode? patternBefore,
        bool patternAfterExists,
        JsonNode? patternAfter,
        bool siblingBeforeExists,
        JsonNode? siblingBefore,
        bool siblingAfterExists,
        JsonNode? siblingAfter)
    {
        var patternUnchanged = patternBeforeExists == patternAfterExists
                               && JsonNode.DeepEquals(patternBefore, patternAfter);
        if (patternUnchanged)
        {
            return siblingBeforeExists == siblingAfterExists
                   && JsonNode.DeepEquals(siblingBefore, siblingAfter);
        }

        if (patternBefore is JsonObject patternBeforeObject
            && patternAfter is JsonObject patternAfterObject
            && siblingBefore is JsonObject siblingBeforeObject
            && siblingAfter is JsonObject siblingAfterObject)
        {
            var keys = patternBeforeObject.Select(static pair => pair.Key)
                .Concat(patternAfterObject.Select(static pair => pair.Key))
                .Concat(siblingBeforeObject.Select(static pair => pair.Key))
                .Concat(siblingAfterObject.Select(static pair => pair.Key))
                .Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var hasPatternBefore = patternBeforeObject.TryGetPropertyValue(key, out var patternBeforeValue);
                var hasPatternAfter = patternAfterObject.TryGetPropertyValue(key, out var patternAfterValue);
                var hasSiblingBefore = siblingBeforeObject.TryGetPropertyValue(key, out var siblingBeforeValue);
                var hasSiblingAfter = siblingAfterObject.TryGetPropertyValue(key, out var siblingAfterValue);
                if (!ReplaysExactPatch(
                        hasPatternBefore,
                        patternBeforeValue,
                        hasPatternAfter,
                        patternAfterValue,
                        hasSiblingBefore,
                        siblingBeforeValue,
                        hasSiblingAfter,
                        siblingAfterValue))
                {
                    return false;
                }
            }

            return true;
        }

        return siblingBeforeExists == patternBeforeExists
               && siblingAfterExists == patternAfterExists
               && JsonNode.DeepEquals(siblingBefore, patternBefore)
               && JsonNode.DeepEquals(siblingAfter, patternAfter);
    }

    private readonly record struct McpCallIdentity(string Server, string Kind, string Method);

    private sealed record RepeatedMcpRepairPattern(
        McpCallIdentity BeforeIdentity,
        McpCallIdentity AfterIdentity,
        JsonObject BeforeShell,
        JsonObject AfterShell);

    private static void ValidateRepairStepList(
        IReadOnlyList<StepDef> original,
        IReadOnlyList<StepDef> candidate,
        string path,
        string? targetStepId,
        RepeatedMcpRepairPattern? repeatedMcpRepair,
        ref bool changedTargetRegion)
    {
        if (original.Count != candidate.Count)
        {
            throw RepairScopeViolation(
                path,
                "A surgical repair must preserve step count, order, and topology.");
        }

        for (var index = 0; index < original.Count; index++)
        {
            var before = original[index];
            var after = candidate[index];
            var stepPath = $"{path}[{index}]";
            if (!string.Equals(before.Id, after.Id, StringComparison.Ordinal)
                || !string.Equals(before.Type, after.Type, StringComparison.Ordinal))
            {
                throw RepairScopeViolation(
                    stepPath,
                    "A surgical repair cannot rename, replace, reorder, add, or remove steps.");
            }

            ValidateLocalWorkflowCallEdge(before, after, stepPath);

            var isTarget = !string.IsNullOrWhiteSpace(targetStepId)
                           && string.Equals(before.Id, targetStepId, StringComparison.Ordinal);
            var isExistingDirectConsumer = !string.IsNullOrWhiteSpace(targetStepId)
                                           && ReferencesStep(BuildStepShell(before).ToJsonString(), targetStepId);
            var isRepeatedMcpContractRepair = !isTarget
                                              && IsExactRepeatedMcpRepair(before, after, repeatedMcpRepair);
            var beforeShell = BuildStepShell(before);
            var afterShell = BuildStepShell(after);
            if (!JsonNode.DeepEquals(beforeShell, afterShell))
            {
                if (!isTarget && !isExistingDirectConsumer && !isRepeatedMcpContractRepair)
                {
                    throw RepairScopeViolation(
                        stepPath,
                        $"Step '{before.Id}' is outside the failing step's repair region and must remain unchanged.");
                }

                changedTargetRegion = true;
            }

            // The target is expected to be a leaf operation. Even if a model
            // returns a composite here, its topology remains locked below.
            ValidateRepairStepList(before.Steps ?? [], after.Steps ?? [], stepPath + ".steps", targetStepId, repeatedMcpRepair, ref changedTargetRegion);
            ValidateRepairStepList(before.Default ?? [], after.Default ?? [], stepPath + ".default", targetStepId, repeatedMcpRepair, ref changedTargetRegion);
            ValidateBranches(before.Branches, after.Branches, stepPath + ".branches", targetStepId, repeatedMcpRepair, ref changedTargetRegion);
            ValidateCases(before.Cases, after.Cases, stepPath + ".cases", targetStepId, repeatedMcpRepair, ref changedTargetRegion);
        }
    }

    private static void ValidateLocalWorkflowCallEdge(StepDef before, StepDef after, string path)
    {
        if (!string.Equals(before.Type, "workflow.call", StringComparison.OrdinalIgnoreCase)
            || before.Input?["ref"] is not JsonObject beforeRef)
            return;

        var kind = TryGetString(beforeRef["kind"]) ?? "local";
        if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase))
            return;

        var afterRef = after.Input?["ref"] as JsonObject;
        if (!JsonNode.DeepEquals(beforeRef, afterRef))
        {
            throw RepairScopeViolation(
                path + ".input.ref",
                "A surgical repair must preserve every existing local workflow.call edge.");
        }
    }

    private static void ValidateBranches(
        IReadOnlyList<BranchDef>? original,
        IReadOnlyList<BranchDef>? candidate,
        string path,
        string? targetStepId,
        RepeatedMcpRepairPattern? repeatedMcpRepair,
        ref bool changedTargetRegion)
    {
        var before = original ?? [];
        var after = candidate ?? [];
        if (before.Count != after.Count)
            throw RepairScopeViolation(path, "Parallel branch topology must remain unchanged.");
        for (var index = 0; index < before.Count; index++)
            ValidateRepairStepList(before[index].Steps, after[index].Steps, $"{path}[{index}].steps", targetStepId, repeatedMcpRepair, ref changedTargetRegion);
    }

    private static void ValidateCases(
        IReadOnlyList<SwitchCaseDef>? original,
        IReadOnlyList<SwitchCaseDef>? candidate,
        string path,
        string? targetStepId,
        RepeatedMcpRepairPattern? repeatedMcpRepair,
        ref bool changedTargetRegion)
    {
        var before = original ?? [];
        var after = candidate ?? [];
        if (before.Count != after.Count)
            throw RepairScopeViolation(path, "Switch case topology must remain unchanged.");
        for (var index = 0; index < before.Count; index++)
        {
            EnsureEquivalent(before[index].Value, after[index].Value, $"{path}[{index}].value");
            EnsureEquivalent(before[index].When, after[index].When, $"{path}[{index}].when");
            ValidateRepairStepList(before[index].Steps, after[index].Steps, $"{path}[{index}].steps", targetStepId, repeatedMcpRepair, ref changedTargetRegion);
        }
    }

    private static JsonObject BuildStepShell(StepDef step)
        => new()
        {
            ["id"] = step.Id,
            ["type"] = step.Type,
            ["if"] = step.If,
            ["input"] = step.Input?.DeepClone(),
            ["output"] = step.Output,
            ["output_schema"] = step.OutputSchema?.DeepClone(),
            ["retry"] = BuildRetryNode(step.Retry),
            ["on_error"] = BuildOnErrorNode(step.OnError),
            ["expr"] = step.Expr,
            ["item_var"] = step.ItemVar,
            ["index_var"] = step.IndexVar
        };

    private static bool EquivalentOutputContractCore(OutputDef left, OutputDef right)
        => string.Equals(left.Type, right.Type, StringComparison.Ordinal)
           && left.Nullable == right.Nullable
           && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
           && EquivalentOutputContract(left.Items, right.Items)
           && EquivalentOutputContracts(left.Properties, right.Properties)
           && EquivalentOutputContract(left.AdditionalProperties, right.AdditionalProperties)
           && EquivalentStrings(left.RequiredProperties, right.RequiredProperties);

    private static bool EquivalentOutputContract(OutputDef? left, OutputDef? right)
        => left is null || right is null
            ? left is null && right is null
            : EquivalentOutputContractCore(left, right);

    private static bool EquivalentOutputContracts(
        IReadOnlyDictionary<string, OutputDef>? left,
        IReadOnlyDictionary<string, OutputDef>? right,
        bool includeExpressions = false)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.Count != right.Count || left.Keys.Any(key => !right.ContainsKey(key)))
            return false;
        return left.All(pair =>
            EquivalentOutputContractCore(pair.Value, right[pair.Key])
            && (!includeExpressions || string.Equals(pair.Value.Expr, right[pair.Key].Expr, StringComparison.Ordinal)));
    }

    private static bool ReferencesStep(string? value, string stepId)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains($"data.steps.{stepId}", StringComparison.Ordinal);

    private static void EnsureEquivalent<T>(T original, T candidate, string path)
    {
        if (!Equivalent(original, candidate))
            throw RepairScopeViolation(path, $"'{path}' is outside the failing step's repair region and must remain unchanged.");
    }

    private static bool Equivalent<T>(T original, T candidate)
        => EquivalentValue(original, candidate);

    private static bool EquivalentValue(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return (left, right) switch
        {
            (JsonNode leftNode, JsonNode rightNode) => JsonNode.DeepEquals(leftNode, rightNode),
            (WorkflowSkillDef leftSkill, WorkflowSkillDef rightSkill) => EquivalentSkill(leftSkill, rightSkill),
            (IReadOnlyDictionary<string, string> leftMap, IReadOnlyDictionary<string, string> rightMap) => EquivalentStringMap(leftMap, rightMap),
            (IReadOnlyDictionary<string, InputDef> leftInputs, IReadOnlyDictionary<string, InputDef> rightInputs) => EquivalentInputs(leftInputs, rightInputs),
            (IReadOnlyDictionary<string, OutputDef> leftOutputs, IReadOnlyDictionary<string, OutputDef> rightOutputs) => EquivalentOutputContracts(leftOutputs, rightOutputs, includeExpressions: true),
            (IReadOnlyList<string> leftStrings, IReadOnlyList<string> rightStrings) => EquivalentStrings(leftStrings, rightStrings),
            (RetryPolicy leftRetry, RetryPolicy rightRetry) => EquivalentRetry(leftRetry, rightRetry),
            (OnErrorDef leftHandler, OnErrorDef rightHandler) => EquivalentOnError(leftHandler, rightHandler),
            _ => Equals(left, right)
        };
    }

    private static bool EquivalentSkill(WorkflowSkillDef left, WorkflowSkillDef right)
        => string.Equals(left.Description, right.Description, StringComparison.Ordinal)
           && EquivalentStrings(left.Tags, right.Tags)
           && EquivalentInputs(left.Inputs, right.Inputs)
           && EquivalentOutputContracts(left.Outputs, right.Outputs, includeExpressions: true);

    private static bool EquivalentStringMap(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
           && left.All(pair => right.TryGetValue(pair.Key, out var value)
                               && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool EquivalentInputs(
        IReadOnlyDictionary<string, InputDef>? left,
        IReadOnlyDictionary<string, InputDef>? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return left.Count == right.Count
               && left.All(pair => right.TryGetValue(pair.Key, out var value)
                                   && EquivalentInput(pair.Value, value));
    }

    private static bool EquivalentInput(InputDef left, InputDef right)
        => string.Equals(left.Type, right.Type, StringComparison.Ordinal)
           && left.Required == right.Required
           && left.Nullable == right.Nullable
           && Equals(left.Default, right.Default)
           && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
           && (left.Items is null || right.Items is null
               ? left.Items is null && right.Items is null
               : EquivalentInput(left.Items, right.Items))
           && EquivalentInputs(left.Properties, right.Properties)
           && (left.AdditionalProperties is null || right.AdditionalProperties is null
               ? left.AdditionalProperties is null && right.AdditionalProperties is null
               : EquivalentInput(left.AdditionalProperties, right.AdditionalProperties))
           && EquivalentStrings(left.RequiredProperties, right.RequiredProperties);

    private static bool EquivalentStrings(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => left is null || right is null
            ? left is null && right is null
            : left.SequenceEqual(right, StringComparer.Ordinal);

    private static bool EquivalentRetry(RetryPolicy? left, RetryPolicy? right)
        => left is null || right is null
            ? left is null && right is null
            : left.Max == right.Max
              && left.BackoffMs == right.BackoffMs
              && left.BackoffMult.Equals(right.BackoffMult)
              && left.JitterMs == right.JitterMs;

    private static bool EquivalentOnError(OnErrorDef? left, OnErrorDef? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.Cases.Count != right.Cases.Count)
            return false;
        for (var index = 0; index < left.Cases.Count; index++)
        {
            var before = left.Cases[index];
            var after = right.Cases[index];
            if (!string.Equals(before.If, after.If, StringComparison.Ordinal)
                || !string.Equals(before.Action, after.Action, StringComparison.Ordinal)
                || !JsonNode.DeepEquals(before.SetOutput, after.SetOutput)
                || !EquivalentRetry(before.Retry, after.Retry))
                return false;
        }
        return true;
    }

    private static JsonNode? BuildRetryNode(RetryPolicy? retry)
        => retry is null
            ? null
            : new JsonObject
            {
                ["max"] = retry.Max,
                ["backoff_ms"] = retry.BackoffMs,
                ["backoff_mult"] = retry.BackoffMult,
                ["jitter_ms"] = retry.JitterMs
            };

    private static JsonNode? BuildOnErrorNode(OnErrorDef? onError)
    {
        if (onError is null)
            return null;
        return new JsonArray(onError.Cases.Select(item => (JsonNode)new JsonObject
        {
            ["if"] = item.If,
            ["action"] = item.Action,
            ["set_output"] = item.SetOutput?.DeepClone(),
            ["retry"] = BuildRetryNode(item.Retry)
        }).ToArray());
    }

    private static WorkflowRuntimeException RepairScopeViolation(string path, string message)
        => new(
            ErrorCodes.TemplatePlan,
            $"Surgical workflow repair scope violation at '{path}': {message}",
            details: new JsonObject
            {
                ["code"] = "REPAIR_SCOPE_VIOLATION",
                ["phase"] = "validation",
                ["path"] = path,
                ["message"] = message,
                ["suggestion"] = "Preserve every workflow and step, and change only the identified failing step or an existing direct consumer expression."
            });
}
