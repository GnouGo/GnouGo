using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowEngineTests
{
    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    private static WorkflowEngine CreateEngine(ILLMClient? llm = null)
    {
        return new WorkflowEngine { LLMClient = llm };
    }

    // === Template Render Tests ===

    [Fact]
    public async Task Execute_TemplateRender_ProducesText()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: greet
        type: template.render
        input:
          engine: mustache
          template: ""Hello {{name}}""
          data:
            name: ""${data.inputs.name}""
          mode: text
    outputs:
      text: ""${data.steps.greet.text}""
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["name"] = "World" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Hello World", result.Outputs!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_TemplateRender_JsonMode_ParsesJson()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: json_step
        type: template.render
        input:
          engine: mustache
          template: '{""value"": {{x}}}'
          data:
            x: ""${data.inputs.x}""
          mode: json
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["x"] = 42 }, CancellationToken.None);

        Assert.True(result.Success);
        var stepOutput = result.Outputs!["json_step"] as JsonObject;
        Assert.NotNull(stepOutput?["json"]);
    }

    // === If Guard Tests ===

    [Fact]
    public async Task Execute_IfGuard_True_StepExecutes()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        if: ""${true}""
        input:
          engine: mustache
          template: ok
          mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(StepStatus.Succeeded, result.StepResults[0].Status);
    }

    [Fact]
    public async Task Execute_IfGuard_False_StepSkipped()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        if: ""${false}""
        input:
          engine: mustache
          template: ok
          mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(StepStatus.Skipped, result.StepResults[0].Status);
    }

    // === Sequence Tests ===

    [Fact]
    public async Task Execute_Sequence_ExecutesInOrder()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: seq
        type: sequence
        steps:
          - id: a
            type: template.render
            input:
              engine: mustache
              template: A
              mode: text
          - id: b
            type: template.render
            input:
              engine: mustache
              template: B
              mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
    }

    // === Parallel Tests ===

    [Fact]
    public async Task Execute_Parallel_AllBranchesRun()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: par
        type: parallel
        branches:
          - steps:
              - id: b1
                type: template.render
                input:
                  engine: mustache
                  template: branch1
                  mode: text
          - steps:
              - id: b2
                type: template.render
                input:
                  engine: mustache
                  template: branch2
                  mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
    }

    // === Switch Tests ===

    [Fact]
    public async Task Execute_Switch_FormA_MatchesValue()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: sw
        type: switch
        expr: ""${data.inputs.mode}""
        cases:
          - value: fast
            steps:
              - id: fast_s
                type: template.render
                input:
                  engine: mustache
                  template: FAST
                  mode: text
          - value: slow
            steps:
              - id: slow_s
                type: template.render
                input:
                  engine: mustache
                  template: SLOW
                  mode: text
    outputs:
      result: ""${data.steps.sw}""
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["mode"] = "fast" }, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Execute_Switch_FormB_WhenCondition()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: sw
        type: switch
        cases:
          - when: ""${data.inputs.x > 10}""
            steps:
              - id: high
                type: template.render
                input:
                  engine: mustache
                  template: HIGH
                  mode: text
          - when: ""${true}""
            steps:
              - id: low
                type: template.render
                input:
                  engine: mustache
                  template: LOW
                  mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["x"] = 20 }, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Execute_Switch_Default_WhenNoMatch()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: sw
        type: switch
        expr: ""${data.inputs.val}""
        cases:
          - value: a
            steps:
              - id: ca
                type: template.render
                input:
                  engine: mustache
                  template: A
                  mode: text
        default:
          - id: def
            type: template.render
            input:
              engine: mustache
              template: DEFAULT
              mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["val"] = "z" }, CancellationToken.None);
        Assert.True(result.Success);
    }

    // === Loop Tests ===

    [Fact]
    public async Task Execute_LoopParallel_IteratesItems()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.parallel
        input:
          items: ""${data.inputs.items}""
        item_var: item
        index_var: idx
        steps:
          - id: render
            type: template.render
            input:
              engine: mustache
              template: ""{{val}}""
              data:
                val: ""${data.item}""
              mode: text
    outputs:
      count: ""${data.steps.loop.count}""
");
        var engine = CreateEngine();
        var items = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3));
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["items"] = items }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(3, result.Outputs!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Execute_LoopSequential_WhileCondition()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.sequential
        input:
          times: 3
        steps:
          - id: inner
            type: template.render
            input:
              engine: mustache
              template: iteration
              mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Execute_LoopSequential_WhileCanUseLoopIndexFromZero()
    {
        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: loop
         type: loop.sequential
         input:
           while: ""${data._loop.index < 3}""
         steps:
           - id: inner
             type: template.render
             input:
               engine: mustache
               template: ""Iteration {{idx}}""
               data:
                 idx: ""${data._loop.index}""
               mode: text
     outputs:
       count: ""${data.steps.loop.count}""
 ");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.Outputs!["count"]!.GetValue<int>());

        var loopOutput = result.StepResults[0].Output as JsonObject;
        var iterations = loopOutput?["results"] as JsonArray;
        Assert.NotNull(iterations);
        Assert.Equal("Iteration 0", iterations[0]?["inner"]?["text"]?.GetValue<string>());
    }

    // === LLM Call Tests ===

    [Fact]
    public async Task Execute_LlmCall_CallsMockLLM()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "AI response", Usage = new JsonObject { ["tokens"] = 100 } });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          model: gpt-4
          prompt: ""Hello LLM""
    outputs:
      answer: ""${data.steps.ask.text}""
");
        var engine = CreateEngine(mockLlm.Object);
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("AI response", result.Outputs!["answer"]!.GetValue<string>());
        mockLlm.Verify(l => l.CallAsync(It.Is<LLMRequest>(r => r.Model == "gpt-4" && r.Prompt == "Hello LLM"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_LlmCall_WithExpressionInPrompt()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "response" });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          model: gpt-4
          prompt: ""Analyze: ${data.inputs.text}""
          temperature: 0.5
");
        var engine = CreateEngine(mockLlm.Object);
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["text"] = "important data" }, CancellationToken.None);

        Assert.True(result.Success);
        mockLlm.Verify(l => l.CallAsync(
            It.Is<LLMRequest>(r => r.Prompt == "Analyze: important data" && Math.Abs((r.Temperature ?? 0) - 0.5) < 0.0001),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_LlmCall_UsesRuntimeDefaults_WhenProviderAndModelAreOmitted()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "defaulted" });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          prompt: ""Hello defaults""
    outputs:
      answer: ""${data.steps.ask.text}""
");
        var engine = CreateEngine(mockLlm.Object);
        engine.LlmDefaults = new LlmRuntimeDefaults
        {
            Provider = "openai",
            Model = "gpt-4o-mini"
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("defaulted", result.Outputs!["answer"]!.GetValue<string>());
        mockLlm.Verify(l => l.CallAsync(
            It.Is<LLMRequest>(r =>
                r.Provider == "openai"
                && r.Model == "gpt-4o-mini"
                && r.Prompt == "Hello defaults"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // === Workflow Call Tests ===

    [Fact]
    public async Task Execute_WorkflowCall_Local_CallsSubWorkflow()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref:
            kind: local
            name: helper
          args:
            x: ""${data.inputs.val}""
    outputs:
      result: ""${data.steps.call_helper.outputs.text}""
  helper:
    inputs:
      x:
        type: string
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          template: ""got: {{x}}""
          data:
            x: ""${data.inputs.x}""
          mode: text
    outputs:
      text: ""${data.steps.render.text}""
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["val"] = "hello" }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("got: hello", result.Outputs!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_WorkflowCall_Local_PreservesDecimalInputAsChildNumber()
    {
        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            inputs:
              number_of_issues_to_process: number
            steps:
              - id: list_and_filter_issues
                type: workflow.call
                input:
                  ref: { kind: local, name: list_and_filter_issues }
                  args:
                    max_issues_to_process: ${data.inputs.number_of_issues_to_process}
            outputs:
              limit: ${data.steps.list_and_filter_issues.outputs.limit}
          list_and_filter_issues:
            inputs:
              max_issues_to_process:
                type: number
                required: true
            steps:
              - id: compute_limit
                type: set
                input:
                  limit: ${data.inputs.max_issues_to_process}
            outputs:
              limit: ${data.steps.compute_limit.limit}
        """);

        var result = await CreateEngine().ExecuteAsync(
            wf,
            new JsonObject { ["number_of_issues_to_process"] = 2m },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Outputs!["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task Execute_WorkflowCall_Local_AppliesDefaultsAndValidatesChildInputs()
    {
        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: call_helper
                type: workflow.call
                input:
                  ref: { kind: local, name: helper }
                  args: {}
            outputs:
              count: ${data.steps.call_helper.outputs.count}
          helper:
            inputs:
              count:
                type: number
                required: false
                default: 3
            steps:
              - id: result
                type: set
                input:
                  count: ${data.inputs.count}
            outputs:
              count: ${data.steps.result.count}
        """);

        var result = await CreateEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.Outputs!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Execute_WorkflowCall_Local_RejectsMissingRequiredChildInput()
    {
        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: call_helper
                type: workflow.call
                input:
                  ref: { kind: local, name: helper }
                  args: {}
          helper:
            inputs:
              count:
                type: number
                required: true
            steps:
              - id: result
                type: set
                input:
                  count: ${data.inputs.count}
        """);

        var result = await CreateEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        var error = Assert.IsType<WorkflowError>(result.Error);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, error.Code);
        Assert.Contains("called workflow 'helper'", error.Message);
        Assert.Contains("Input 'count' is required", error.Message);
        Assert.Equal("helper", error.Details!["workflow"]!.GetValue<string>());
    }

    // === WFScript (Jint) Integration Tests ===

    [Fact]
    public async Task Execute_WithGlobalFunctions_FunctionsAvailable()
    {
        var wf = CompileMain(@"
version: 1
functions: |
  function greet(name) {
    return ""Hello "" + name;
  }
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: ""{{msg}}""
          data:
            msg: ""${functions.greet(data.inputs.name)}""
          mode: text
    outputs:
      text: ""${data.steps.s1.text}""
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["name"] = "Alice" }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("Hello Alice", result.Outputs!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_WithGlobalFunctions_CanUseUrlConstructor()
    {
        var wf = CompileMain(@"
version: 1
functions: |
  function parseGithubRepoUrl(url) {
    const u = new URL(url);
    const parts = u.pathname.replace(/^\/+/, '').split('/');
    return { owner: parts[0], repo: parts[1] };
  }
workflows:
  main:
    steps:
      - id: parsed
        type: set
        input:
          owner: ""${functions.parseGithubRepoUrl(data.inputs.repo_url).owner}""
          repo: ""${functions.parseGithubRepoUrl(data.inputs.repo_url).repo}""
    outputs:
      owner: ""${data.steps.parsed.owner}""
      repo: ""${data.steps.parsed.repo}""
");

        var result = await CreateEngine().ExecuteAsync(
            wf,
            new JsonObject { ["repo_url"] = "https://github.com/AxaFrance/oidc-client" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("AxaFrance", result.Outputs!["owner"]!.GetValue<string>());
        Assert.Equal("oidc-client", result.Outputs["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_WithLocalFunctions_ShadowsGlobal()
    {
        var wf = CompileMain(@"
version: 1
functions: |
  function tag() { return ""global""; }
workflows:
  main:
    functions: |
      function tag() { return ""local""; }
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: ""{{val}}""
          data:
            val: ""${functions.tag()}""
          mode: text
    outputs:
      val: ""${data.steps.s1.text}""
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("local", result.Outputs!["val"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_WorkflowCall_UsesCalledWorkflowLocalFunctions()
    {
        var wf = CompileMain(@"
version: 1
functions: |
  function tag() { return ""global""; }
workflows:
  main:
    functions: |
      function tag() { return ""main""; }
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args: {}
      - id: caller_tag
        type: set
        input:
          value: ""${functions.tag()}""
    outputs:
      helper_tag: ""${data.steps.call_helper.outputs.tag}""
      caller_tag: ""${data.steps.caller_tag.value}""
  helper:
    functions: |
      function tag() { return ""helper""; }
    steps:
      - id: value
        type: set
        input:
          tag: ""${functions.tag()}""
    outputs:
      tag: ""${data.steps.value.tag}""
");
        var result = await CreateEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("helper", result.Outputs!["helper_tag"]!.GetValue<string>());
        Assert.Equal("main", result.Outputs!["caller_tag"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_WorkflowCall_DoesNotLeakCallerFunctionsIntoCalledWorkflow()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    functions: |
      function callerOnly() { return ""caller""; }
    steps:
      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args: {}
  helper:
    steps:
      - id: value
        type: set
        input:
          val: ""${functions.callerOnly()}""
");
        var result = await CreateEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.EvalError, result.Error!.Code);
        Assert.Contains("callerOnly", result.Error.Message);
    }

    [Fact]
    public async Task Execute_ParallelWorkflowCalls_KeepLocalFunctionScopesIsolated()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: fanout
        type: parallel
        branches:
          - steps:
              - id: call_a
                type: workflow.call
                input:
                  ref: { kind: local, name: a }
                  args: {}
          - steps:
              - id: call_b
                type: workflow.call
                input:
                  ref: { kind: local, name: b }
                  args: {}
    outputs:
      a: ""${data.steps.fanout.branches[0].call_a.outputs.tag}""
      b: ""${data.steps.fanout.branches[1].call_b.outputs.tag}""
  a:
    functions: |
      function tag() { return ""a""; }
    steps:
      - id: value
        type: set
        input:
          tag: ""${functions.tag()}""
    outputs:
      tag: ""${data.steps.value.tag}""
  b:
    functions: |
      function tag() { return ""b""; }
    steps:
      - id: value
        type: set
        input:
          tag: ""${functions.tag()}""
    outputs:
      tag: ""${data.steps.value.tag}""
");
        var result = await CreateEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("a", result.Outputs!["a"]!.GetValue<string>());
        Assert.Equal("b", result.Outputs!["b"]!.GetValue<string>());
    }

    // === Error Handling Tests ===

    [Fact]
    public async Task Execute_UnknownStepType_Fails()
    {
        var doc = WorkflowParser.Parse(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
");
        // Manually create a compiled workflow with an unknown type
        var compiled = new CompiledDocument { Entrypoint = "main" };
        compiled.Workflows["main"] = new CompiledWorkflow
        {
            Name = "main",
            Source = doc.Workflows["main"],
            Steps = new List<CompiledStep>
            {
                new() { Source = new StepDef { Id = "bad", Type = "nonexistent" } }
            },
            Document = compiled
        };

        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.StepTypeUnknown, result.Error!.Code);
    }

    [Fact]
    public async Task Execute_Cancellation_Fails()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: ok
          mode: text
");
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), cts.Token);
        Assert.False(result.Success);
        Assert.Equal("CANCELLED", result.Error!.Code);
    }

    [Fact]
    public async Task Execute_StepLimit_Exceeded_Fails()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: a
          mode: text
      - id: s2
        type: template.render
        input:
          engine: mustache
          template: b
          mode: text
      - id: s3
        type: template.render
        input:
          engine: mustache
          template: c
          mode: text
");
        var engine = CreateEngine();
        engine.Limits = new ExecutionLimits { MaxTotalStepsExecuted = 2 };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.False(result.Success);
    }

    // === Output Tests ===

    [Fact]
    public async Task Execute_NoOutputsDefined_ReturnsAllSteps()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: hello
          mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.NotNull(result.Outputs);
    }

    [Fact]
    public async Task Execute_WithOutputAlias_WritesAlias()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        output: myAlias
        input:
          engine: mustache
          template: hello
          mode: text
");
        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);
    }

    // === OnError Tests ===

    [Fact]
    public async Task Execute_OnError_Continue_SkipsFailure()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("network failure"));

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: llm.call
        input:
          model: gpt-4
          prompt: test
        on_error:
          cases:
            - action: continue
              set_output: fallback
      - id: s2
        type: template.render
        input:
          engine: mustache
          template: after_error
          mode: text
""");
        var engine = CreateEngine(mockLlm.Object);
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        // The on_error with continue should allow workflow to proceed
        Assert.True(result.Success || result.StepResults.Count > 1);
    }

    [Fact]
    public async Task Execute_LoopParallel_OnErrorObjectSetOutput_CanUseErrorAndItemContext()
    {
        var registry = new StepExecutorRegistry();
        registry.Register(new GnOuGo.Flow.Core.Runtime.Executors.LoopParallelExecutor());
        registry.Register(new ThrowingStepExecutor(
            "fail.step",
            new WorkflowRuntimeException("MCP_TIMEOUT", "simulated timeout", retryable: true)));

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: fetch_pages
        type: loop.parallel
        input:
          items:
            - url: https://slimfaas.dev/docs
          max_concurrency: 1
        item_var: item
        index_var: idx
        steps:
          - id: fetch_page
            type: fail.step
            on_error:
              cases:
                - if: "${error.code == \"MCP_TIMEOUT\"}"
                  action: continue
                  set_output:
                    status: error
                    response:
                      url: "${data.item.url}"
                      error_code: "${error.code}"
                      error_message: "${error.message}"
""");

        var engine = new WorkflowEngine(registry);
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var fetchPages = Assert.IsType<JsonObject>(result.Outputs!["fetch_pages"]);
        var results = Assert.IsType<JsonArray>(fetchPages["results"]);
        var firstIterationSteps = Assert.IsType<JsonObject>(results[0]);
        var fetchPage = Assert.IsType<JsonObject>(firstIterationSteps["fetch_page"]);
        var response = Assert.IsType<JsonObject>(fetchPage["response"]);

        Assert.Equal("error", fetchPage["status"]!.GetValue<string>());
        Assert.Equal("https://slimfaas.dev/docs", response["url"]!.GetValue<string>());
        Assert.Equal("MCP_TIMEOUT", response["error_code"]!.GetValue<string>());
        Assert.Equal("simulated timeout", response["error_message"]!.GetValue<string>());
    }

    // === StepExecutorRegistry Tests ===

    [Fact]
    public void StepExecutorRegistry_Get_ReturnsExecutor()
    {
        var registry = new StepExecutorRegistry();
        var executor = new Mock<IStepExecutor>();
        executor.Setup(e => e.StepType).Returns("custom.type");
        registry.Register(executor.Object);
        Assert.Same(executor.Object, registry.Get("custom.type"));
    }

    [Fact]
    public void StepExecutorRegistry_Get_UnknownType_ReturnsNull()
    {
        var registry = new StepExecutorRegistry();
        Assert.Null(registry.Get("nonexistent"));
    }

    private sealed class ThrowingStepExecutor : IStepExecutor
    {
        private readonly Exception _exception;

        public ThrowingStepExecutor(string stepType, Exception exception)
        {
            StepType = stepType;
            _exception = exception;
        }

        public string StepType { get; }

        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
            => Task.FromException<JsonNode?>(_exception);
    }
}
