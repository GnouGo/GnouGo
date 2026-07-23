using System.Text.Json.Nodes;

namespace GnOuGo.Assets.Animation.Preview;

public sealed class WorkflowPreviewDocument
{
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public string? Entrypoint { get; set; }
    public Dictionary<string, WorkflowPreviewDefinition> Workflows { get; } = new(StringComparer.Ordinal);
    public List<WorkflowPreviewUnknownField> UnknownFields { get; } = [];
}

public sealed class WorkflowPreviewDefinition
{
    public JsonObject? Inputs { get; set; }
    public List<WorkflowPreviewStep> Steps { get; set; } = [];
}

public sealed class WorkflowPreviewStep
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? If { get; set; }
    public string? Expr { get; set; }
    public JsonNode? Input { get; set; }
    public string? ItemVar { get; set; }
    public string? IndexVar { get; set; }
    public List<WorkflowPreviewStep>? Steps { get; set; }
    public List<WorkflowPreviewBranch>? Branches { get; set; }
    public List<WorkflowPreviewCase>? Cases { get; set; }
    public List<WorkflowPreviewStep>? Default { get; set; }
}

public sealed class WorkflowPreviewBranch
{
    public string? Name { get; set; }
    public List<WorkflowPreviewStep> Steps { get; set; } = [];
}

public sealed class WorkflowPreviewCase
{
    public string? Value { get; set; }
    public string? When { get; set; }
    public List<WorkflowPreviewStep> Steps { get; set; } = [];
}

public sealed record WorkflowPreviewUnknownField(string Path, string Field);

public sealed class WorkflowPreviewParseException(string message) : Exception(message);

public enum WorkflowPreviewDiagnosticSeverity
{
    Warning,
    Error
}

public sealed record WorkflowPreviewDiagnostic(
    string Code,
    string Message,
    WorkflowPreviewDiagnosticSeverity Severity,
    string? WorkflowName = null,
    string? StepId = null,
    string? Field = null);

public sealed class WorkflowPreviewValidationResult
{
    public required WorkflowPreviewDocument Document { get; init; }
    public required IReadOnlyList<WorkflowPreviewDiagnostic> Diagnostics { get; init; }
    public bool IsValid => Diagnostics.All(static diagnostic => diagnostic.Severity != WorkflowPreviewDiagnosticSeverity.Error);
    public string? Entrypoint => Document.Entrypoint;
    public IReadOnlyList<WorkflowPreviewFailureTarget> FailureTargets { get; init; } = [];
}

public sealed record WorkflowPreviewFailureTarget(string WorkflowName, string StepId, string StepType, string Label);
