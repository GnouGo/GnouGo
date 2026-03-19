using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Compilation;

/// <summary>
/// Validation error for a workflow document.
/// </summary>
public sealed class ValidationError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? WorkflowName { get; set; }
    public string? StepId { get; set; }
    public string? Field { get; set; }

    public override string ToString() =>
        $"[{Code}] {(WorkflowName != null ? $"workflow '{WorkflowName}'" : "")}" +
        $"{(StepId != null ? $" step '{StepId}'" : "")} - {Message}";
}

