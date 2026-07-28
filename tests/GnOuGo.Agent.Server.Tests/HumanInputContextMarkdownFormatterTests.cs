using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class HumanInputContextMarkdownFormatterTests
{
    [Fact]
    public void Format_MarkdownSummaryWithListsAndColons_RemainsMarkdown()
    {
        const string context = """
## Final answer

- Status: ready
- Export: optional
""";

        var markdown = HumanInputContextMarkdownFormatter.Format(context);

        Assert.Equal(context, markdown);
        Assert.DoesNotContain("```yaml", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WorkflowYaml_UsesCodeFenceWithoutGeneratingMermaid()
    {
        const string context = """
version: 1
entrypoint: main
workflows:
  main:
    steps:
      - id: answer
        type: set
""";

        var markdown = HumanInputContextMarkdownFormatter.Format(context);

        Assert.StartsWith("```yaml\n", markdown, StringComparison.Ordinal);
        Assert.Contains("entrypoint: main", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Workflow diagrams", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_JsonContext_PrettyPrintsOnce()
    {
        const string context = """{"status":"ready","count":2}""";

        var markdown = HumanInputContextMarkdownFormatter.Format(context);

        Assert.StartsWith("```json\n", markdown, StringComparison.Ordinal);
        Assert.Contains("\n  \"status\": \"ready\",", markdown, StringComparison.Ordinal);
        Assert.EndsWith("\n```", markdown, StringComparison.Ordinal);
    }
}
