using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Definition of a single step (node) in a workflow.
/// </summary>
public sealed class StepDef
{
    /// <summary>Unique step identifier within its workflow.</summary>
    public string Id { get; set; } = "";

    /// <summary>Step type (e.g. "sequence", "parallel", "llm.call", etc.).</summary>
    public string Type { get; set; } = "";

    /// <summary>Optional conditional guard expression (${...}).</summary>
    public string? If { get; set; }

    /// <summary>Input data — YAML values with ${...} expressions at any depth.</summary>
    public JsonNode? Input { get; set; }

    /// <summary>Optional output alias name.</summary>
    public string? Output { get; set; }

    /// <summary>Optional JSON Schema contract for set step output.</summary>
    public JsonNode? OutputSchema { get; set; }

    /// <summary>Retry policy.</summary>
    public RetryPolicy? Retry { get; set; }

    /// <summary>Error handler.</summary>
    public OnErrorDef? OnError { get; set; }

    // === Composite-specific fields ===

    /// <summary>Sub-steps for sequence / loop / switch default.</summary>
    public List<StepDef>? Steps { get; set; }

    /// <summary>Branches for parallel step.</summary>
    public List<BranchDef>? Branches { get; set; }

    /// <summary>Cases for switch step.</summary>
    public List<SwitchCaseDef>? Cases { get; set; }

    /// <summary>Switch expression (form A).</summary>
    public string? Expr { get; set; }

    /// <summary>Default branch for switch.</summary>
    public List<StepDef>? Default { get; set; }

    /// <summary>Item variable name for loop.parallel.</summary>
    public string? ItemVar { get; set; }

    /// <summary>Index variable name for loop.parallel.</summary>
    public string? IndexVar { get; set; }
}

/// <summary>
/// A branch in a parallel step.
/// </summary>
public sealed class BranchDef
{
    public List<StepDef> Steps { get; set; } = new();
}

/// <summary>
/// A case in a switch step.
/// </summary>
public sealed class SwitchCaseDef
{
    /// <summary>Literal value match (form A).</summary>
    public string? Value { get; set; }

    /// <summary>Boolean expression guard (form B).</summary>
    public string? When { get; set; }

    /// <summary>Steps to execute if matched.</summary>
    public List<StepDef> Steps { get; set; } = new();
}
