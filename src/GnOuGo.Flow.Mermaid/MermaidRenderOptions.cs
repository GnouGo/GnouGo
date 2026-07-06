namespace GnOuGo.Flow.Mermaid;

/// <summary>
/// Options used by <see cref="MermaidWorkflowRenderer"/>.
/// </summary>
public sealed class MermaidRenderOptions
{
    /// <summary>
    /// Workflow to render as the main diagram. Defaults to document entrypoint,
    /// then "main", then the first workflow in the document.
    /// </summary>
    public string? Entrypoint { get; set; }

    /// <summary>Flowchart layout direction.</summary>
    public MermaidDirection Direction { get; set; } = MermaidDirection.TopDown;

    /// <summary>Controls additional local workflow diagrams.</summary>
    public MermaidSubWorkflowMode SubWorkflowMode { get; set; } = MermaidSubWorkflowMode.ReferencedLocalOnly;

    /// <summary>Include workflow input/output summary nodes.</summary>
    public bool IncludeInputsAndOutputs { get; set; }

    /// <summary>Include step <c>if</c> guards in node labels.</summary>
    public bool IncludeConditions { get; set; } = true;

    /// <summary>Maximum Mermaid label length before truncation.</summary>
    public int MaxLabelLength { get; set; } = 120;
}
