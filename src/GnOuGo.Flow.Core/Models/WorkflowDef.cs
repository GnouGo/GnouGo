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

    /// <summary>Output mapping expressions.</summary>
    public Dictionary<string, string>? Outputs { get; set; }
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

