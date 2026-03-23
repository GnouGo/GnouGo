namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Definition of a single workflow within a document.
/// </summary>
public sealed class WorkflowDef
{
    /// <summary>Input parameter declarations.</summary>
    public Dictionary<string, InputDef>? Inputs { get; set; }

    /// <summary>Local WFScript functions (shadow global).</summary>
    public string? Functions { get; set; }

    /// <summary>Ordered list of steps.</summary>
    public List<StepDef> Steps { get; set; } = new();

    /// <summary>Typed output declarations. Each value contains the expression and optional type schema.</summary>
    public Dictionary<string, OutputDef>? Outputs { get; set; }
}

/// <summary>
/// Input parameter definition. Supports short form (type only) and long form
/// with rich type descriptors for arrays, objects, and dictionaries.
/// </summary>
public sealed class InputDef
{
    /// <summary>Base type: string, number, boolean, array, object, dictionary, any.</summary>
    public string Type { get; set; } = "any";

    /// <summary>Whether the input is required (default true).</summary>
    public bool Required { get; set; } = true;

    /// <summary>Default value when the caller does not supply one.</summary>
    public object? Default { get; set; }

    /// <summary>Element type descriptor (only when Type == "array").</summary>
    public InputDef? Items { get; set; }

    /// <summary>Named property schemas (only when Type == "object").</summary>
    public Dictionary<string, InputDef>? Properties { get; set; }

    /// <summary>
    /// Value type descriptor for dictionary entries (only when Type == "dictionary")
    /// or for extra properties of an object.
    /// </summary>
    public InputDef? AdditionalProperties { get; set; }

    /// <summary>List of required property names (only when Type == "object").</summary>
    public List<string>? RequiredProperties { get; set; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Output parameter definition. Contains the runtime expression and an optional type schema
/// that enables automatic JSON Schema generation for MCP tool exposure.
/// Supports short form (expression only: <c>key: "${expr}"</c>) and long form with type descriptors.
/// </summary>
public sealed class OutputDef
{
    /// <summary>Runtime expression evaluated against the data context (e.g. <c>"${data.steps.x.text}"</c>).</summary>
    public string Expr { get; set; } = "";

    /// <summary>Base type: string, number, boolean, array, object, dictionary, any.</summary>
    public string Type { get; set; } = "any";

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Element type descriptor (only when Type == "array").</summary>
    public OutputDef? Items { get; set; }

    /// <summary>Named property schemas (only when Type == "object").</summary>
    public Dictionary<string, OutputDef>? Properties { get; set; }

    /// <summary>
    /// Value type descriptor for dictionary entries (only when Type == "dictionary")
    /// or for extra properties of an object.
    /// </summary>
    public OutputDef? AdditionalProperties { get; set; }

    /// <summary>List of required property names (only when Type == "object").</summary>
    public List<string>? RequiredProperties { get; set; }

    /// <summary>Shorthand factory: creates an untyped OutputDef from a bare expression string.</summary>
    public static OutputDef FromExpr(string expr) => new() { Expr = expr };
}

