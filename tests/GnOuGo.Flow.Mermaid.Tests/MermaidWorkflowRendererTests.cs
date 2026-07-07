using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Mermaid;
using Xunit;

namespace GnOuGo.Flow.Mermaid.Tests;

public sealed class MermaidWorkflowRendererTests
{
    [Fact]
    public void Render_GeneratesMainDiagramAndReferencedLocalSubWorkflow()
    {
        var yaml = """
version: 1
name: local-call-demo
workflows:
  main:
    steps:
      - id: gather
        type: template.render
        input:
          engine: mustache
          template: "hello"
          mode: text
      - id: call_helper
        type: workflow.call
        input:
          ref:
            kind: local
            name: helper
          args:
            topic: "${data.steps.gather.text}"
  helper:
    steps:
      - id: summarize
        type: llm.call
        input:
          model: mock
          prompt: "summarize"
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Equal("main", result.Main.WorkflowName);
        Assert.Equal("main.mmd", result.Main.SuggestedFileName);
        Assert.Contains("flowchart TD", result.Main.Content);
        Assert.Contains("gather - template.render", result.Main.Content);
        Assert.Contains("call_helper - workflow.call - local: helper", result.Main.Content);
        Assert.Contains("-->", result.Main.Content);

        var subWorkflow = Assert.Single(result.SubWorkflows);
        Assert.Equal("helper", subWorkflow.WorkflowName);
        Assert.Equal("helper.mmd", subWorkflow.SuggestedFileName);
        Assert.Contains("summarize - llm.call", subWorkflow.Content);

        Assert.Equal(new[] { "helper" }, result.ReferencedLocalWorkflows);
        Assert.Empty(result.MissingLocalWorkflowReferences);
    }

    [Fact]
    public void Render_ReferencedLocalOnly_RecursivelyIncludesNestedLocalReferences()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: call_a
        type: workflow.call
        input:
          ref: { kind: local, name: a }
  a:
    steps:
      - id: call_b
        type: workflow.call
        input:
          ref: { kind: local, name: b }
  b:
    steps:
      - id: done
        type: set
        input:
          value: true
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Equal(new[] { "a", "b" }, result.SubWorkflows.Select(static diagram => diagram.WorkflowName).ToArray());
        Assert.Equal(new[] { "a", "b" }, result.ReferencedLocalWorkflows);
    }

    [Fact]
    public void Render_AllLocalWorkflows_IncludesUnreferencedLocalWorkflows()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: start
        type: set
        input:
          value: true
  helper:
    steps:
      - id: h1
        type: set
        input:
          value: 1
  unused:
    steps:
      - id: u1
        type: set
        input:
          value: 2
""";

        var result = MermaidWorkflowRenderer.Render(yaml, new MermaidRenderOptions
        {
            SubWorkflowMode = MermaidSubWorkflowMode.AllLocalWorkflows
        });

        Assert.Equal(new[] { "helper", "unused" }, result.SubWorkflows.Select(static diagram => diagram.WorkflowName).ToArray());
    }

    [Fact]
    public void Render_WorkflowRoute_StaticLocalCandidatesGenerateSubWorkflowDiagrams()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: local, name: inspect_repo }
              description: Inspect a repo.
            - ref: { kind: database }
              tags_any: [documents]
  inspect_repo:
    steps:
      - id: inspect
        type: mcp.call
        input:
          server: git
          method: status
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Contains("route - workflow.route - routes: inspect_repo", result.Main.Content);
        var subWorkflow = Assert.Single(result.SubWorkflows);
        Assert.Equal("inspect_repo", subWorkflow.WorkflowName);
    }

    [Fact]
    public void Render_RemoteWorkflowCall_DoesNotGenerateSubWorkflow()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: call_remote
        type: workflow.call
        input:
          ref:
            kind: url
            url: https://example.test/workflow.yaml
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Empty(result.SubWorkflows);
        Assert.Empty(result.ReferencedLocalWorkflows);
        Assert.Contains("call_remote - workflow.call - url: https://example.test/workflow.yaml", result.Main.Content);
    }

    [Fact]
    public void Render_CanRenderParsedDocument()
    {
        var document = WorkflowParser.Parse("""
version: 1
workflows:
  custom:
    steps:
      - id: step_one
        type: emit
        input:
          message: "working"
""");

        var result = MermaidWorkflowRenderer.Render(document, new MermaidRenderOptions
        {
            Entrypoint = "custom",
            Direction = MermaidDirection.LeftRight
        });

        Assert.Equal("custom", result.Main.WorkflowName);
        Assert.Contains("flowchart LR", result.Main.Content);
        Assert.Contains("step_one - emit", result.Main.Content);
    }
}
