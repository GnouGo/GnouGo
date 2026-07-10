namespace GnOuGo.Flow.Mermaid;

/// <summary>
/// Controls how step <c>if</c> guards are represented in generated Mermaid diagrams.
/// </summary>
public enum MermaidGuardRenderMode
{
    /// <summary>Render guards as labels on the incoming edge for the guarded step.</summary>
    EdgeLabel,

    /// <summary>Render guards inside the guarded step node label.</summary>
    NodeLabel
}
