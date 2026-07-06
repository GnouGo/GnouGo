namespace GnOuGo.Flow.Mermaid;

/// <summary>
/// A generated Mermaid diagram for one workflow.
/// </summary>
public sealed class MermaidDiagram
{
    public string WorkflowName { get; init; } = "";
    public string SuggestedFileName { get; init; } = "";
    public bool IsEntrypoint { get; init; }
    public string Content { get; init; } = "";
}
