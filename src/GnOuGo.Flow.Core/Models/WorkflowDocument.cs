namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Root document of a GnOuGo.Flow YAML workflow definition.
/// </summary>
public sealed class WorkflowDocument
{
    /// <summary>DSL version (must be 1).</summary>
    public int Dsl { get; set; } = 1;

    /// <summary>Optional document name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional metadata.</summary>
    public Dictionary<string, string>? Meta { get; set; }

    /// <summary>Global WFScript functions block (JavaScript via Jint).</summary>
    public string? Functions { get; set; }

    /// <summary>Map of workflow definitions, keyed by name.</summary>
    public Dictionary<string, WorkflowDef> Workflows { get; set; } = new();

    /// <summary>Names of workflows exported for remote calling.</summary>
    public List<string>? Exports { get; set; }

    /// <summary>Entry-point workflow name. Defaults to "main" if present.</summary>
    public string? Entrypoint { get; set; }

    /// <summary>Original YAML source text (preserved for checkpoint/resume).</summary>
    public string? RawYaml { get; set; }
}

