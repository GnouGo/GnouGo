namespace GnOuGo.Flow.Mermaid;

/// <summary>
/// Result of rendering a workflow document to Mermaid diagrams.
/// </summary>
public sealed class MermaidRenderResult
{
    public MermaidDiagram Main { get; init; } = null!;
    public IReadOnlyList<MermaidDiagram> SubWorkflows { get; init; } = Array.Empty<MermaidDiagram>();
    public IReadOnlyList<string> ReferencedLocalWorkflows { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingLocalWorkflowReferences { get; init; } = Array.Empty<string>();

    public IEnumerable<MermaidDiagram> AllDiagrams
    {
        get
        {
            yield return Main;
            foreach (var diagram in SubWorkflows)
                yield return diagram;
        }
    }
}
