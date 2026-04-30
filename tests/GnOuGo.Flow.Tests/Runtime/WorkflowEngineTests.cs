using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
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
        var iterations = loopOutput?["iterations"] as JsonArray;
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
}
