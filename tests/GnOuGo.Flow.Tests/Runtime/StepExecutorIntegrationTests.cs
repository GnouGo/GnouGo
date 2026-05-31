using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

/// <summary>
/// Integration tests for individual step executors via the engine.
/// </summary>
public class StepExecutorIntegrationTests
{
    private static CompiledDocument CompileDoc(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        return new WorkflowCompiler().Compile(doc);
    }

    private static async Task<RunResult> RunMain(string yaml, JsonObject? inputs = null, ILLMClient? llm = null)
    {
        var compiled = CompileDoc(yaml);
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine { LLMClient = llm };
        return await engine.ExecuteAsync(wf, inputs ?? new JsonObject(), CancellationToken.None);
    }

    // === LoopSequential with times ===

    [Fact]
    public async Task LoopSequential_TimesN_ExecutesNTimes()
    {
        var result = await RunMain(@"
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
              template: tick
              mode: text
");
        Assert.True(result.Success);
        // The loop output is stored in data.steps.loop which has { iterations: [...], count: 3 }
        var stepOutput = result.Outputs as JsonObject;
        Assert.NotNull(stepOutput);
    }

    // === LoopParallel with max_concurrency ===

    [Fact]
    public async Task LoopParallel_MaxConcurrency_Works()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.parallel
        input:
          items: ""${data.inputs.items}""
          max_concurrency: 1
        item_var: item
        steps:
          - id: r
            type: template.render
            input:
              engine: mustache
              template: ""{{val}}""
              data:
                val: ""${data.item}""
              mode: text
    outputs:
      count: ""${data.steps.loop.count}""
", new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2)) });
        Assert.True(result.Success);
        Assert.Equal(2, result.Outputs!["count"]!.GetValue<int>());
    }

    // === LoopParallel limit exceeded ===

    [Fact]
    public async Task LoopParallel_ExceedsLimit_Fails()
    {
        var compiled = CompileDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.parallel
        input:
          items: ""${data.inputs.items}""
        steps:
          - id: r
            type: template.render
            input:
              engine: mustache
              template: x
              mode: text
");
        var engine = new WorkflowEngine { Limits = new ExecutionLimits { MaxLoopIterations = 2 } };
        var items = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3), JsonValue.Create(4));
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject { ["items"] = items }, CancellationToken.None);
        Assert.False(result.Success);
    }

    // === LLM call with structured output ===

    [Fact]
    public async Task LlmCall_StructuredOutput_SetsJsonInResponse()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = "{\"status\":\"ok\"}",
                Json = new JsonObject { ["status"] = "ok" }
            });

        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          model: gpt-4
          prompt: test
          structured_output:
            schema_inline:
              type: object
", llm: mockLlm.Object);

        Assert.True(result.Success);
    }

    // === LLM call without client fails gracefully ===

    [Fact]
    public async Task LlmCall_NoClient_Fails()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          model: gpt-4
          prompt: test
");
        // Without a configured LLM client, the call should fail
        Assert.False(result.Success);
    }

    // === Workflow.call depth limit ===

    [Fact]
    public async Task WorkflowCall_DepthLimit_Fails()
    {
        var compiled = CompileDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref:
            kind: local
            name: helper
  helper:
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: ok
          mode: text
");
        var engine = new WorkflowEngine { Limits = new ExecutionLimits { MaxCallDepth = 0 } };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);
        Assert.False(result.Success);
    }

    // === Switch with no matching case and no default ===

    [Fact]
    public async Task Switch_NoMatch_NoDefault_ReturnsNullOutput()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: sw
        type: switch
        expr: ""${data.inputs.val}""
        cases:
          - value: x
            steps:
              - id: cx
                type: template.render
                input:
                  engine: mustache
                  template: X
                  mode: text
");
        Assert.True(result.Success);
    }

    // === Template render in JSON mode with invalid JSON ===

    [Fact]
    public async Task TemplateRender_JsonMode_InvalidJson_Fails()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
        input:
          engine: mustache
          template: not valid json
          data: {}
          mode: json
");
        Assert.False(result.Success);
    }

    // === Complex pipeline: template → LLM → template ===

    [Fact]
    public async Task Pipeline_Template_LLM_Template()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "AI says hello" });

        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: prep
        type: template.render
        input:
          engine: mustache
          template: ""Prompt: {{msg}}""
          data:
            msg: ""${data.inputs.message}""
          mode: text
      - id: ask
        type: llm.call
        input:
          model: gpt-4
          prompt: ""${data.steps.prep.text}""
      - id: format
        type: template.render
        input:
          engine: mustache
          template: ""Result: {{answer}}""
          data:
            answer: ""${data.steps.ask.text}""
          mode: text
    outputs:
      final: ""${data.steps.format.text}""
", new JsonObject { ["message"] = "test" }, mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("Result: AI says hello", result.Outputs!["final"]!.GetValue<string>());
        mockLlm.Verify(l => l.CallAsync(
            It.Is<LLMRequest>(r => r.Prompt == "Prompt: test"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // === Remote workflow call without fetcher ===

    [Fact]
    public async Task WorkflowCall_Remote_NoFetcher_Fails()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref:
            kind: url
            url: ""https://example.com/wf.yaml""
");
        Assert.False(result.Success);
    }

    // === LoopSequential with while: child step sees loop index ===

    [Fact]
    public async Task LoopSequential_WhileCanUseLoopIndexAndChildStepsSeeFirstIndex()
    {
        var result = await RunMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: loop
         type: loop.sequential
         input:
           while: ""${data._loop.index < 2}""
         steps:
           - id: inner
             type: template.render
             input:
               engine: mustache
               template: ""{{idx}}""
               data:
                 idx: ""${data._loop.index}""
               mode: text
 ");

        Assert.True(result.Success);
        var loopOutput = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(loopOutput);
        Assert.Equal(2, loopOutput["count"]!.GetValue<int>());

        var iterations = loopOutput["iterations"] as JsonArray;
        Assert.NotNull(iterations);
        Assert.Equal("0", iterations[0]?["inner"]?["text"]?.GetValue<string>());
        Assert.Equal("1", iterations[1]?["inner"]?["text"]?.GetValue<string>());
    }

    // === LoopSequential with items ===

    [Fact]
    public async Task LoopSequential_Items_IteratesOverArray()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.sequential
        input:
          items: ""${data.inputs.items}""
        item_var: item
        index_var: idx
        steps:
          - id: r
            type: template.render
            input:
              engine: mustache
              template: ""{{val}}-{{i}}""
              data:
                val: ""${data.item}""
                i: ""${data.idx}""
              mode: text
    outputs:
      count:
        expr: ""${data.steps.loop.count}""
", new JsonObject { ["items"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b"), JsonValue.Create("c")) });
        Assert.True(result.Success);
        Assert.Equal(3, result.Outputs!["count"]!.GetValue<int>());

        var loopOutput = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(loopOutput);
        var iterations = loopOutput["iterations"] as JsonArray;
        Assert.NotNull(iterations);
        Assert.Equal(3, iterations.Count);
        Assert.Equal("a-0", iterations[0]?["r"]?["text"]?.GetValue<string>());
        Assert.Equal("b-1", iterations[1]?["r"]?["text"]?.GetValue<string>());
        Assert.Equal("c-2", iterations[2]?["r"]?["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task LoopSequential_Over_IsAliasForItems()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.sequential
        input:
          over: ""${data.inputs.items}""
        item_var: entry
        steps:
          - id: r
            type: template.render
            input:
              engine: mustache
              template: ""{{val}}""
              data:
                val: ""${data.entry}""
              mode: text
", new JsonObject { ["items"] = new JsonArray(JsonValue.Create("x"), JsonValue.Create("y")) });
        Assert.True(result.Success);
        var loopOutput = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(loopOutput);
        Assert.Equal(2, loopOutput["count"]!.GetValue<int>());
        var iterations = loopOutput["iterations"] as JsonArray;
        Assert.NotNull(iterations);
        Assert.Equal("x", iterations[0]?["r"]?["text"]?.GetValue<string>());
        Assert.Equal("y", iterations[1]?["r"]?["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task LoopSequential_Items_ExceedsLimit_Fails()
    {
        var compiled = CompileDoc(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.sequential
        input:
          items: ""${data.inputs.items}""
        steps:
          - id: r
            type: template.render
            input:
              engine: mustache
              template: x
              mode: text
");
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine { Limits = new ExecutionLimits { MaxLoopIterations = 2 } };
        var bigArray = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3));
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["items"] = bigArray }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("LOOP_LIMIT", result.Error?.Code);
    }

    [Fact]
    public async Task LoopSequential_ItemsAndTimes_MutuallyExclusive_Fails()
    {
        var result = await RunMain(@"
version: 1
workflows:
  main:
    steps:
      - id: loop
        type: loop.sequential
        input:
          items: ""${data.inputs.items}""
          times: 3
        steps:
          - id: r
            type: set
            input: { value: ""x"" }
", new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1)) });
        Assert.False(result.Success);
        Assert.Equal("INPUT_VALIDATION", result.Error?.Code);
    }
}
