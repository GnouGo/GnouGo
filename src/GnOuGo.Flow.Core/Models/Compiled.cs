using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Compiled, ready-to-execute document.
/// </summary>
public sealed class CompiledDocument
{
    public WorkflowDocument Source { get; set; } = null!;
    public Dictionary<string, CompiledWorkflow> Workflows { get; set; } = new();
    public string? Entrypoint { get; set; }
}

/// <summary>
/// A compiled workflow ready for execution.
/// </summary>
public sealed class CompiledWorkflow
{
    public string Name { get; set; } = "";
    public WorkflowDef Source { get; set; } = null!;
    public List<CompiledStep> Steps { get; set; } = new();
    public Dictionary<string, OutputDef>? Outputs { get; set; }

    /// <summary>Reference to the parent compiled document (for sub-workflow calls).</summary>
    public CompiledDocument Document { get; set; } = null!;
}

/// <summary>
/// A compiled step with pre-parsed expressions.
/// </summary>
public sealed class CompiledStep
{
    public StepDef Source { get; set; } = null!;
    public string Id => Source.Id;
    public string Type => Source.Type;

    /// <summary>Sub-steps (for sequence, loop).</summary>
    public List<CompiledStep>? Steps { get; set; }

    /// <summary>Branches (for parallel).</summary>
    public List<List<CompiledStep>>? Branches { get; set; }

    /// <summary>Cases (for switch).</summary>
    public List<CompiledSwitchCase>? Cases { get; set; }

    /// <summary>Default branch (for switch).</summary>
    public List<CompiledStep>? Default { get; set; }
}

public sealed class CompiledSwitchCase
{
    public SwitchCaseDef Source { get; set; } = null!;
    public List<CompiledStep> Steps { get; set; } = new();
}

/// <summary>
/// Result of a workflow run.
/// </summary>
public sealed class RunResult
{
    public bool Success { get; set; }
    public JsonNode? Outputs { get; set; }
    public List<StepResult> StepResults { get; set; } = new();
    public WorkflowError? Error { get; set; }
}

/// <summary>
/// Result of a single step execution.
/// </summary>
public sealed class StepResult
{
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public StepStatus Status { get; set; }
    public JsonNode? Output { get; set; }
    public WorkflowError? Error { get; set; }
    public TimeSpan Duration { get; set; }
}

public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>
/// Workflow error with standard code.
/// </summary>
public sealed class WorkflowError
{
    public string Code { get; set; } = "";
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Retryable { get; set; }
    public JsonNode? Details { get; set; }
}

/// <summary>
/// Execution limits / quotas.
/// </summary>
public sealed class ExecutionLimits
{
    public int MaxTotalStepsExecuted { get; set; } = 10_000;
    public int MaxCallDepth { get; set; } = 20;
    public int MaxParallelBranches { get; set; } = 50;
    public int MaxLoopIterations { get; set; } = 1_000;
    public int MaxExpressionAstNodes { get; set; } = 500;
    public int MaxExpressionStatements { get; set; } = 100_000;
    public int ExpressionTimeoutSeconds { get; set; } = 15;
    public int ExpressionMemoryLimitBytes { get; set; } = 1_000_000_000;
    public int MaxSwitchCases { get; set; } = 100;
    public int MaxFunctionCallDepth { get; set; } = 50;

    /// <summary>
    /// When true, step inputs and outputs are logged as OpenTelemetry span events
    /// (gen_ai.content.prompt / gen_ai.content.completion convention).
    /// Disabled by default because payloads can be large.
    /// </summary>
    public bool LogStepContent { get; set; }

    /// <summary>
    /// Unique identifier for this workflow run. Used by human-in-the-loop
    /// providers to route responses to the correct waiting step.
    /// </summary>
    public string? RunId { get; set; }
}

