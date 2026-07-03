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

    /// <summary>
    /// Structural YAML fields that the parser did not map into the workflow model.
    /// They are collected instead of ignored so validation can fail with a precise
    /// diagnostic when generated YAML places a key at the wrong level.
    /// </summary>
    public List<UnknownYamlField> UnknownFields { get; } = new();
}

/// <summary>
/// A YAML mapping key found at a structural level where the DSL does not define it.
/// Free-form maps such as step <c>input</c>, metadata, and JSON schemas are handled
/// by their own validators and are not reported through this type.
/// </summary>
public sealed class UnknownYamlField
{
    public string Path { get; set; } = "";
    public string Field { get; set; } = "";
    public IReadOnlyList<string> AllowedFields { get; set; } = Array.Empty<string>();
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
