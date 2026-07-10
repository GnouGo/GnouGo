using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowMermaidMarkdownFormatterTests
{
    [Fact]
    public void AppendDiagrams_ValidWorkflowYaml_AppendsMainDiagramFence()
    {
        const string yaml = """
version: 1
workflows:
  main:
    steps:
      - id: answer
        type: set
        input:
          value: ok
""";

        var markdown = WorkflowMermaidMarkdownFormatter.AppendDiagrams(
            "Here is the result.",
            yaml,
            NullLogger.Instance);

        Assert.Contains("Here is the result.", markdown);
        Assert.Contains("## Workflow diagrams", markdown);
        Assert.Contains("### Main workflow: `main`", markdown);
        Assert.Contains("```mermaid", markdown);
        Assert.Contains("answer - set", markdown);
    }

    [Fact]
    public void AppendDiagrams_LocalWorkflowCall_ByDefaultOmitsLocalSubWorkflowFence()
    {
        const string yaml = """
version: 1
workflows:
  main:
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
  helper:
    steps:
      - id: helper_step
        type: set
        input:
          value: ok
""";

        var markdown = WorkflowMermaidMarkdownFormatter.AppendDiagrams(
            "Generated workflow.",
            yaml,
            NullLogger.Instance);

        Assert.Contains("### Main workflow: `main`", markdown);
        Assert.DoesNotContain("### Local workflow: `helper`", markdown);
        Assert.Equal(1, CountOccurrences(markdown, "```mermaid"));
    }

    [Fact]
    public void AppendDiagrams_WhenLocalWorkflowDetailsEnabled_AppendsLocalSubWorkflowFence()
    {
        const string yaml = """
version: 1
workflows:
  main:
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
  helper:
    steps:
      - id: helper_step
        type: set
        input:
          value: ok
""";

        var markdown = WorkflowMermaidMarkdownFormatter.AppendDiagrams(
            "Generated workflow.",
            yaml,
            NullLogger.Instance,
            new WorkflowMermaidMarkdownOptions { IncludeLocalWorkflowDetails = true });

        Assert.Contains("### Main workflow: `main`", markdown);
        Assert.Contains("### Local workflow: `helper`", markdown);
        Assert.Equal(2, CountOccurrences(markdown, "```mermaid"));
    }

    [Fact]
    public void AppendDiagrams_HidesEmitStepsByDefault()
    {
        const string yaml = """
version: 1
workflows:
  main:
    steps:
      - id: status
        type: emit
        input:
          message: "working"
      - id: answer
        type: set
        input:
          value: ok
""";

        var markdown = WorkflowMermaidMarkdownFormatter.AppendDiagrams(
            "Generated workflow.",
            yaml,
            NullLogger.Instance);

        Assert.DoesNotContain("status - emit", markdown);
        Assert.Contains("answer - set", markdown);
    }

    [Fact]
    public void AppendDiagrams_InvalidWorkflowYaml_ReturnsOriginalMarkdown()
    {
        const string original = "Keep this response.";

        var markdown = WorkflowMermaidMarkdownFormatter.AppendDiagrams(
            original,
            "not: [valid",
            NullLogger.Instance);

        Assert.Equal(original, markdown);
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}
