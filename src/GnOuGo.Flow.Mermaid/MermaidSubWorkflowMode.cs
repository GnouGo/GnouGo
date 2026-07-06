namespace GnOuGo.Flow.Mermaid;

/// <summary>
/// Controls which local workflows are emitted as additional Mermaid diagrams.
/// </summary>
public enum MermaidSubWorkflowMode
{
    None,
    ReferencedLocalOnly,
    AllLocalWorkflows
}
