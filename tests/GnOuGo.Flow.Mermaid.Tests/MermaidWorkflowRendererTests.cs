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
    public void Render_CanRenderParsedDocument_AndHidesEmitStepsByDefault()
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
        Assert.DoesNotContain("step_one - emit", result.Main.Content);
    }

    [Fact]
    public void Render_HiddenEmitStep_PreservesGraphContinuity()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: first
        type: set
        input:
          value: one
      - id: status
        type: emit
        input:
          message: "working"
      - id: last
        type: set
        input:
          value: two
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Contains("first - set", result.Main.Content);
        Assert.DoesNotContain("status - emit", result.Main.Content);
        Assert.Contains("last - set", result.Main.Content);
        Assert.Contains("n2_first --> n3_last", result.Main.Content);
    }

    [Fact]
    public void Render_IncludeEmitSteps_RestoresEmitNodes()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: status
        type: emit
        input:
          message: "working"
""";

        var result = MermaidWorkflowRenderer.Render(yaml, new MermaidRenderOptions
        {
            IncludeEmitSteps = true
        });

        Assert.Contains("status - emit", result.Main.Content);
    }

    [Fact]
    public void Render_UsesDistinctShapesForKnownStepCategories()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: assign_value
        type: set
        input: { value: ok }
      - id: render_text
        type: template.render
        input: { engine: mustache, template: "ok", mode: text }
      - id: ask_user
        type: human.input
        input: { prompt: "Continue?" }
      - id: ask_model
        type: llm.call
        input: { model: mock, prompt: "ok" }
      - id: call_tool
        type: mcp.call
        input: { server: git, method: status }
      - id: call_workflow
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
      - id: repeat
        type: loop.sequential
        input: { times: 1 }
        steps:
          - id: loop_step
            type: set
            input: { value: ok }
      - id: group
        type: sequence
        steps:
          - id: grouped_step
            type: set
            input: { value: ok }
      - id: decide
        type: switch
        cases:
          - value: ok
            steps:
              - id: case_step
                type: set
                input: { value: ok }
  helper:
    steps:
      - id: helper_step
        type: set
        input: { value: ok }
""";

        var result = MermaidWorkflowRenderer.Render(yaml, new MermaidRenderOptions
        {
            SubWorkflowMode = MermaidSubWorkflowMode.None
        });

        Assert.Contains("assign_value(\"assign_value - set\")", result.Main.Content);
        Assert.Contains("render_text[/\"render_text - template.render\"/]", result.Main.Content);
        Assert.Contains("ask_user[/\"ask_user - human.input\"\\]", result.Main.Content);
        Assert.Contains("ask_model>\"ask_model - llm.call\"]", result.Main.Content);
        Assert.Contains("call_tool[(\"call_tool - mcp.call\")]", result.Main.Content);
        Assert.Contains("call_workflow[[\"call_workflow - workflow.call - local: helper\"]]", result.Main.Content);
        Assert.Contains("repeat{{\"repeat - loop.sequential\"}}", result.Main.Content);
        Assert.Contains("group([\"group - sequence\"])", result.Main.Content);
        Assert.Contains("decide{\"decide - switch\"}", result.Main.Content);
    }

    [Fact]
    public void Render_IfGuard_DefaultsToIncomingEdgeLabel()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: guarded
        type: set
        if: "${data.inputs.enabled}"
        input:
          value: ok
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Contains("n0_start -->|\"if: ${data.inputs.enabled}\"| n2_guarded", result.Main.Content);
        Assert.Contains("guarded - set", result.Main.Content);
        Assert.DoesNotContain("guarded - set - if:", result.Main.Content);
    }

    [Fact]
    public void Render_IfGuard_QuotesMermaidSyntaxCharactersInEdgeLabel()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: first
        type: set
        input:
          value: ok
      - id: guarded
        type: set
        if: '${data.steps.first.url != ""}'
        input:
          value: ok
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Contains(
            "-->|\"if: ${data.steps.first.url != ''}\"| n3_guarded",
            result.Main.Content);
        Assert.DoesNotContain(
            "-->|if: ${data.steps.first.url",
            result.Main.Content);
    }

    [Fact]
    public void Render_NestedSwitchLoopsParallelAndSequence_RejoinCleanly()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: choose
        type: switch
        cases:
          - value: fast
            steps:
              - id: repeat_fast
                type: loop.sequential
                input: { times: 1 }
                steps:
                  - id: fast_step
                    type: set
                    input: { value: fast }
          - value: fanout
            steps:
              - id: parallel_work
                type: parallel
                branches:
                  - steps:
                      - id: branch_one
                        type: set
                        input: { value: one }
                  - steps:
                      - id: branch_two
                        type: set
                        input: { value: two }
        default:
          - id: fallback_group
            type: sequence
            steps:
              - id: fallback_step
                type: set
                input: { value: fallback }
      - id: done
        type: set
        input: { value: done }
""";

        var result = MermaidWorkflowRenderer.Render(yaml);

        Assert.Contains("choose{\"choose - switch\"}", result.Main.Content);
        Assert.Contains("repeat_fast{{\"repeat_fast - loop.sequential\"}}", result.Main.Content);
        Assert.Contains("Loop exit", result.Main.Content);
        Assert.Contains("-->|\"fast\"|", result.Main.Content);
        Assert.Contains("-->|\"fanout\"|", result.Main.Content);
        Assert.Contains("-->|\"default\"|", result.Main.Content);
        Assert.Contains("-->|\"branch 1\"|", result.Main.Content);
        Assert.Contains("-->|\"branch 2\"|", result.Main.Content);
        Assert.Contains("done - set", result.Main.Content);
    }
}
