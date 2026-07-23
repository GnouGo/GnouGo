using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation;

internal static class WorkflowVisualFilter
{
    public static bool IsLongRunningStepType(string? stepType) =>
        !string.IsNullOrWhiteSpace(stepType)
        && (stepType.Equals("llm", StringComparison.OrdinalIgnoreCase)
            || stepType.StartsWith("llm.", StringComparison.OrdinalIgnoreCase)
            || stepType.Equals("mcp", StringComparison.OrdinalIgnoreCase)
            || stepType.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase));

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
        if (IsLongRunningStepType(step.Type))
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
