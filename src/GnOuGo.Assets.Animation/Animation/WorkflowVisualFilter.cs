using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation;

internal static class WorkflowVisualFilter
{
    public static bool IsOrchestrationStepType(string? stepType) =>
        stepType is not null
        && (stepType.Equals("workflow.plan", StringComparison.OrdinalIgnoreCase)
            || stepType.Equals("workflow.route", StringComparison.OrdinalIgnoreCase)
            || stepType.Equals("workflow.execute", StringComparison.OrdinalIgnoreCase));

    public static bool IsLongRunningStepType(string? stepType) =>
        !string.IsNullOrWhiteSpace(stepType)
        && (stepType.Equals("llm", StringComparison.OrdinalIgnoreCase)
            || stepType.StartsWith("llm.", StringComparison.OrdinalIgnoreCase)
            || stepType.Equals("mcp", StringComparison.OrdinalIgnoreCase)
            || stepType.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)
            || stepType.Equals("human", StringComparison.OrdinalIgnoreCase)
            || stepType.StartsWith("human.", StringComparison.OrdinalIgnoreCase));

    public static bool IsVisibleStepType(string? stepType) =>
        IsLongRunningStepType(stepType) || IsOrchestrationStepType(stepType);

    public static AnimationStationKind StationKindFor(string stepType)
    {
        if (stepType.StartsWith("human.", StringComparison.OrdinalIgnoreCase))
            return AnimationStationKind.Human;
        return stepType.ToLowerInvariant() switch
        {
            "workflow.plan" => AnimationStationKind.Planning,
            "workflow.route" => AnimationStationKind.Mcp,
            "workflow.call" or "workflow.execute" => AnimationStationKind.HandoffDesk,
            _ => AnimationStationKind.KeyboardDesk
        };
    }

    public static bool StepsContainVisibleWork(
        WorkflowPreviewDocument document,
        IEnumerable<WorkflowPreviewStep> steps) =>
        steps.Any(step => StepContainsVisibleWork(document, step));

    public static bool StepContainsVisibleWork(
        WorkflowPreviewDocument document,
        WorkflowPreviewStep step)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(step);
        return StepContainsVisibleWork(document, step, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool StepContainsVisibleWork(
        WorkflowPreviewDocument document,
        WorkflowPreviewStep step,
        HashSet<string> visitedWorkflows)
    {
        if (IsVisibleStepType(step.Type))
            return true;

        if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal)
            && WorkflowPreviewValidator.TryGetLocalWorkflowName(step, out var target)
            && document.Workflows.TryGetValue(target, out var workflow)
            && visitedWorkflows.Add(target))
        {
            var result = workflow.Steps.Any(child =>
                StepContainsVisibleWork(document, child, visitedWorkflows));
            visitedWorkflows.Remove(target);
            if (result)
                return true;
        }

        if (step.Steps?.Any(child =>
                StepContainsVisibleWork(document, child, visitedWorkflows)) == true)
            return true;

        if (step.Branches?.Any(branch =>
                branch.Steps.Any(child =>
                    StepContainsVisibleWork(document, child, visitedWorkflows))) == true)
            return true;

        if (step.Cases?.Any(switchCase =>
                switchCase.Steps.Any(child =>
                    StepContainsVisibleWork(document, child, visitedWorkflows))) == true)
            return true;

        return step.Default?.Any(child =>
            StepContainsVisibleWork(document, child, visitedWorkflows)) == true;
    }
}
