using Bunit;
using GnOuGo.Agent.Server.Components.Markdown;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MarkdownContentTests : BunitContext
{
    [Fact]
    public void MermaidFence_RendersAsPreElementUsedByClientEnhancer()
    {
        var cut = Render<MarkdownContent>(parameters => parameters
            .Add(component => component.Content, """
                ```mermaid
                flowchart TD
                    start(("Start")) --> finish(("End"))
                ```
                """));

        var mermaid = cut.Find("pre.mermaid");

        Assert.StartsWith("flowchart TD", mermaid.TextContent.Trim(), StringComparison.Ordinal);
    }
}
