using System.Text.Json.Nodes;

namespace GnOuGo.Assets.Animation.Preview;

public static class WorkflowPreviewValidator
{
    private static readonly HashSet<string> CompositeTypes = new(StringComparer.Ordinal)
    {
        "sequence", "parallel", "loop.sequential", "loop.parallel", "switch"
    };

    public static WorkflowPreviewValidationResult Validate(WorkflowPreviewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = new List<WorkflowPreviewDiagnostic>();
        var failureTargets = new List<WorkflowPreviewFailureTarget>();

        if (document.Version != 1)
            Error("PREVIEW_VERSION", $"Unsupported preview version '{document.Version}'. Only version 1 is supported.");
        if (document.Workflows.Count == 0)
            Error("NO_WORKFLOWS", "At least one workflow is required.");
        if (string.IsNullOrWhiteSpace(document.Entrypoint) || !document.Workflows.ContainsKey(document.Entrypoint))
            Error("INVALID_ENTRYPOINT", $"Entrypoint '{document.Entrypoint ?? "(missing)"}' does not identify a workflow.");

        foreach (var unknown in document.UnknownFields)
        {
            diagnostics.Add(new WorkflowPreviewDiagnostic(
                "UNKNOWN_FIELD",
                $"Field '{unknown.Field}' at '{unknown.Path}' is ignored by the animation preview.",
                WorkflowPreviewDiagnosticSeverity.Warning,
                Field: unknown.Path == "$" ? unknown.Field : $"{unknown.Path}.{unknown.Field}"));
        }

        foreach (var (workflowName, workflow) in document.Workflows)
        {
            if (workflow.Steps.Count == 0)
                Error("EMPTY_STEPS", "Workflow must contain at least one step.", workflowName);

            var ids = new HashSet<string>(StringComparer.Ordinal);
            ValidateSteps(workflow.Steps, workflowName, ids, diagnostics, failureTargets, document);
        }

        DetectWorkflowCallCycles(document, diagnostics);
        return new WorkflowPreviewValidationResult
        {
            Document = document,
            Diagnostics = diagnostics,
            FailureTargets = failureTargets
        };

        void Error(string code, string message, string? workflowName = null) =>
            diagnostics.Add(new WorkflowPreviewDiagnostic(code, message, WorkflowPreviewDiagnosticSeverity.Error, workflowName));
    }

    public static WorkflowPreviewValidationResult ParseAndValidate(string yaml)
    {
        try
        {
            return Validate(WorkflowPreviewParser.Parse(yaml));
        }
        catch (WorkflowPreviewParseException exception)
        {
            return new WorkflowPreviewValidationResult
            {
                Document = new WorkflowPreviewDocument(),
                Diagnostics =
                [
                    new WorkflowPreviewDiagnostic(
                        "YAML_PARSE",
                        exception.Message,
                        WorkflowPreviewDiagnosticSeverity.Error)
                ]
            };
        }
    }

    private static void ValidateSteps(
        IReadOnlyList<WorkflowPreviewStep> steps,
        string workflowName,
        HashSet<string> ids,
        List<WorkflowPreviewDiagnostic> diagnostics,
        List<WorkflowPreviewFailureTarget> failureTargets,
        WorkflowPreviewDocument document)
    {
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                Add("STEP_ID_REQUIRED", "Step ID is required.", step);
            else if (!ids.Add(step.Id))
                Add("DUPLICATE_STEP_ID", $"Step ID '{step.Id}' is used more than once in this workflow.", step);

            if (string.IsNullOrWhiteSpace(step.Type))
                Add("STEP_TYPE_REQUIRED", $"Step '{step.Id}' requires a type.", step);

            switch (step.Type)
            {
                case "sequence" when step.Steps is not { Count: > 0 }:
                    Add("MISSING_STEPS", "Sequence requires non-empty 'steps'.", step);
                    break;
                case "parallel" when step.Branches is not { Count: > 0 }:
                    Add("MISSING_BRANCHES", "Parallel requires non-empty 'branches'.", step);
                    break;
                case "loop.sequential" or "loop.parallel" when step.Steps is not { Count: > 0 }:
                    Add("MISSING_STEPS", $"{step.Type} requires non-empty 'steps'.", step);
                    break;
                case "switch" when step.Cases is not { Count: > 0 } && step.Default is not { Count: > 0 }:
                    Add("MISSING_CASES", "Switch requires at least one case or default branch.", step);
                    break;
            }

            if (string.Equals(step.Type, "workflow.call", StringComparison.Ordinal))
                ValidateWorkflowCall(step, workflowName, document, diagnostics);

            if (!CompositeTypes.Contains(step.Type))
                failureTargets.Add(new WorkflowPreviewFailureTarget(workflowName, step.Id, step.Type, $"{workflowName} / {step.Id}"));

            if (step.Steps is { } childSteps)
                ValidateSteps(childSteps, workflowName, ids, diagnostics, failureTargets, document);
            if (step.Branches is { } branches)
            {
                foreach (var branch in branches)
                    ValidateSteps(branch.Steps, workflowName, ids, diagnostics, failureTargets, document);
            }
            if (step.Cases is { } cases)
            {
                foreach (var switchCase in cases)
                    ValidateSteps(switchCase.Steps, workflowName, ids, diagnostics, failureTargets, document);
            }
            if (step.Default is { } defaultSteps)
                ValidateSteps(defaultSteps, workflowName, ids, diagnostics, failureTargets, document);
        }

        void Add(string code, string message, WorkflowPreviewStep step) => diagnostics.Add(new WorkflowPreviewDiagnostic(
            code, message, WorkflowPreviewDiagnosticSeverity.Error, workflowName, step.Id));
    }

    private static void ValidateWorkflowCall(
        WorkflowPreviewStep step,
        string workflowName,
        WorkflowPreviewDocument document,
        List<WorkflowPreviewDiagnostic> diagnostics)
    {
        if (!TryGetLocalWorkflowName(step, out var target))
        {
            diagnostics.Add(new WorkflowPreviewDiagnostic(
                "DYNAMIC_WORKFLOW_CALL",
                "The workflow target is dynamic or remote and will use a generic simulated subordinate.",
                WorkflowPreviewDiagnosticSeverity.Warning,
                workflowName,
                step.Id));
            return;
        }

        if (!document.Workflows.ContainsKey(target))
        {
            diagnostics.Add(new WorkflowPreviewDiagnostic(
                "WORKFLOW_NOT_FOUND",
                $"Local workflow '{target}' was not found.",
                WorkflowPreviewDiagnosticSeverity.Error,
                workflowName,
                step.Id,
                "input.ref.name"));
        }
    }

    internal static bool TryGetLocalWorkflowName(WorkflowPreviewStep step, out string target)
    {
        target = "";
        if (step.Input is not JsonObject input || input["ref"] is not JsonObject reference)
            return false;

        var kind = ReadString(reference["kind"]) ?? "local";
        var name = ReadString(reference["name"]);
        if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name))
            return false;

        target = name;
        return true;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void DetectWorkflowCallCycles(
        WorkflowPreviewDocument document,
        List<WorkflowPreviewDiagnostic> diagnostics)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var workflowName in document.Workflows.Keys)
            Visit(workflowName);

        void Visit(string workflowName)
        {
            if (visited.Contains(workflowName))
                return;
            if (!visiting.Add(workflowName))
            {
                if (reported.Add(workflowName))
                {
                    diagnostics.Add(new WorkflowPreviewDiagnostic(
                        "WORKFLOW_CYCLE",
                        $"Local workflow-call cycle detected at '{workflowName}'.",
                        WorkflowPreviewDiagnosticSeverity.Error,
                        workflowName));
                }
                return;
            }

            if (document.Workflows.TryGetValue(workflowName, out var workflow))
            {
                foreach (var target in EnumerateCalls(workflow.Steps))
                    Visit(target);
            }
            visiting.Remove(workflowName);
            visited.Add(workflowName);
        }
    }

    private static IEnumerable<string> EnumerateCalls(IEnumerable<WorkflowPreviewStep> steps)
    {
        foreach (var step in steps)
        {
            if (TryGetLocalWorkflowName(step, out var target))
                yield return target;
            if (step.Steps is { } childSteps)
                foreach (var child in EnumerateCalls(childSteps)) yield return child;
            if (step.Branches is { } branches)
                foreach (var branch in branches)
                    foreach (var child in EnumerateCalls(branch.Steps)) yield return child;
            if (step.Cases is { } cases)
                foreach (var switchCase in cases)
                    foreach (var child in EnumerateCalls(switchCase.Steps)) yield return child;
            if (step.Default is { } defaultSteps)
                foreach (var child in EnumerateCalls(defaultSteps)) yield return child;
        }
    }
}
