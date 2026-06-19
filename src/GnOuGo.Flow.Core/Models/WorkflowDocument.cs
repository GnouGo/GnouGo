namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Root document of a GnOuGo.Flow YAML workflow definition.
/// </summary>
public sealed class WorkflowDocument
{
    /// <summary>Document version (must be 1).</summary>
    public int Version { get; set; } = 1;

    /// <summary>Optional document name.</summary>
    public string? Name { get; set; }

    /// <summary>Optional metadata.</summary>
    public Dictionary<string, string>? Meta { get; set; }

    /// <summary>Advertised skill metadata used by routers and catalogs.</summary>
    public WorkflowSkillDef? Skill { get; set; }

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

    /// <summary>Logical source kind used when this document was loaded, such as workspace, url, or database.</summary>
    public string? SourceKind { get; set; }

    /// <summary>Source path or identifier within <see cref="SourceKind"/>, when available.</summary>
    public string? SourcePath { get; set; }
}

/// <summary>
/// Top-level capability card for a workflow document or persisted agent.
/// It is intentionally lightweight so catalogs can expose it without compiling workflows.
/// </summary>
public sealed class WorkflowSkillDef
{
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public Dictionary<string, InputDef>? Inputs { get; set; }
    public Dictionary<string, OutputDef>? Outputs { get; set; }
}
