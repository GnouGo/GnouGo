using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowTelemetryTests
{
    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    // ────── NullWorkflowTelemetry ──────

    [Fact]
    public void NullTelemetry_IsDefault()
    {
        var engine = new WorkflowEngine();
        Assert.NotNull(engine.Telemetry);
        Assert.IsType<NullWorkflowTelemetry>(engine.Telemetry);
    }

    [Fact]
    public void NullTelemetry_SpansAreDisposable()
    {
        var telemetry = NullWorkflowTelemetry.Instance;
        var wSpan = telemetry.WorkflowStart(new WorkflowTelemetryInfo());
        Assert.NotNull(wSpan);
        var sSpan = telemetry.StepStart(wSpan, new StepTelemetryInfo());
        Assert.NotNull(sSpan);
        telemetry.StepEnd(sSpan, new StepResultInfo());
        sSpan.Dispose();
        telemetry.WorkflowEnd(wSpan, new WorkflowResultInfo());
        wSpan.Dispose();
    }

    // ────── Custom telemetry receives events ──────

    private sealed class TestSpan : IWorkflowSpan, IStepSpan
    {
        public string Name { get; init; } = "";
        public string? ParentName { get; init; }
        public bool Disposed { get; private set; }
        public Dictionary<string, object?> Attributes { get; } = new();
        public List<(string Name, IReadOnlyList<KeyValuePair<string, object?>>? Attributes)> SpanEvents { get; } = new();
        public void SetAttribute(string key, object? value) => Attributes[key] = value;
        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => SpanEvents.Add((name, attributes));
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingTelemetry : IWorkflowTelemetry
    {
        public List<(string Event, object? Info)> Events { get; } = new();

        /// <summary>Captured step spans (accessible after execution to inspect attributes).</summary>
        public List<TestSpan> StepSpans { get; } = new();

        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
        {
            Events.Add(("WorkflowStart", info));
            return new TestSpan { Name = info.WorkflowName };
        }

        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
        {
            Events.Add(("WorkflowEnd", result));
        }

        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
        {
            Events.Add(("StepStart", info));
            var span = new TestSpan { Name = info.StepId, ParentName = (parentSpan as TestSpan)?.Name };
            StepSpans.Add(span);
            return span;
        }

        public void StepEnd(IStepSpan span, StepResultInfo result)
        {
            Events.Add(("StepEnd", result));
        }
    }

    [Fact]
    public async Task CustomTelemetry_ReceivesWorkflowAndStepEvents()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: greet
        type: template.render
        input:
          engine: mustache
          template: ""Hello {{name}}""
          data:
            name: World
          mode: text
");
        var engine = new WorkflowEngine { Telemetry = recording };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        // Should have: WorkflowStart, StepStart, StepEnd, WorkflowEnd
        Assert.True(recording.Events.Count >= 4);
        Assert.Equal("WorkflowStart", recording.Events[0].Event);
        Assert.Equal("StepStart", recording.Events[1].Event);
        Assert.Equal("StepEnd", recording.Events[2].Event);
        Assert.Equal("WorkflowEnd", recording.Events[^1].Event);

        // Verify WorkflowStart info
        var wfInfo = recording.Events[0].Info as WorkflowTelemetryInfo;
        Assert.NotNull(wfInfo);
        Assert.Equal("main", wfInfo!.WorkflowName);

        // Verify StepStart info
        var stepInfo = recording.Events[1].Info as StepTelemetryInfo;
        Assert.NotNull(stepInfo);
        Assert.Equal("greet", stepInfo!.StepId);
        Assert.Equal("template.render", stepInfo.StepType);
        Assert.NotNull(stepInfo.Input);
        Assert.Equal("mustache", stepInfo.Input!["engine"]!.GetValue<string>());

        // Verify StepEnd info
        var stepResult = recording.Events[2].Info as StepResultInfo;
        Assert.NotNull(stepResult);
        Assert.Equal(StepStatus.Succeeded, stepResult!.Status);

        // Verify WorkflowEnd info
        var wfResult = recording.Events[^1].Info as WorkflowResultInfo;
        Assert.NotNull(wfResult);
        Assert.True(wfResult!.Success);
        Assert.Equal(1, wfResult.StepsExecuted);
    }

    [Fact]
    public async Task CustomTelemetry_LlmCall_HasGenAiAttributes()
    {
        var recording = new RecordingTelemetry();

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = "Response",
                Usage = new JsonObject
                {
                    ["prompt_tokens"] = 10,
                    ["completion_tokens"] = 20,
                    ["total_tokens"] = 30
                }
            });

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          model: gpt-4o
          provider: openai
          prompt: ""Hello""
          temperature: 0.7
");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            Telemetry = recording
        };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        // The executor writes attributes directly to the span — verify them
        var llmSpan = recording.StepSpans.FirstOrDefault(s => s.Name == "ask");
        Assert.NotNull(llmSpan);

        // Request attributes (written by executor before the LLM call)
        Assert.Equal("chat", llmSpan!.Attributes["gen_ai.operation.name"]);
        Assert.Equal("openai", llmSpan.Attributes["gen_ai.system"]);
        Assert.Equal("gpt-4o", llmSpan.Attributes["gen_ai.request.model"]);
        Assert.Equal(0.7, llmSpan.Attributes["gen_ai.request.temperature"]);

        // Response attributes (written by executor after the LLM call)
        Assert.Equal("gpt-4o", llmSpan.Attributes["gen_ai.response.model"]);
        Assert.Equal("stop", llmSpan.Attributes["gen_ai.response.finish_reason"]);
        Assert.Equal(10, llmSpan.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(20, llmSpan.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(30, llmSpan.Attributes["gen_ai.usage.total_tokens"]);
    }

    [Fact]
    public async Task LogStepContent_LlmCall_EmitsStandardGenAiPromptAndCompletionEvents()
    {
        var recording = new RecordingTelemetry();

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = "Hello back"
            });

        var wf = CompileMain(@"
 dsl: 1
 workflows:
   main:
     steps:
       - id: ask
         type: llm.call
         input:
           model: gpt-4o
           prompt: ""Hello""
 ");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            Telemetry = recording,
            Limits = new ExecutionLimits { LogStepContent = true }
        };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);

        var llmSpan = recording.StepSpans.Single(s => s.Name == "ask");
        Assert.Contains(llmSpan.SpanEvents, e => e.Name == "gen_ai.content.prompt"
            && e.Attributes != null
            && e.Attributes.Any(kv => kv.Key == "gen_ai.prompt" && (string?)kv.Value == "Hello"));
        Assert.Contains(llmSpan.SpanEvents, e => e.Name == "gen_ai.content.completion"
            && e.Attributes != null
            && e.Attributes.Any(kv => kv.Key == "gen_ai.completion" && (string?)kv.Value == "Hello back"));
    }

    [Fact]
    public async Task CustomTelemetry_McpCall_HasMcpAttributes()
    {
        var recording = new RecordingTelemetry();

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("my-server", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "ping" } }
        });

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: my-server
          method: ping
");
        var engine = new WorkflowEngine
        {
            McpClientFactory = factory,
            Telemetry = recording
        };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        // The executor writes attributes directly to the span
        var mcpSpan = recording.StepSpans.FirstOrDefault(s => s.Name == "call");
        Assert.NotNull(mcpSpan);

        Assert.Equal("tool_call", mcpSpan!.Attributes["gen_ai.operation.name"]);
        Assert.Equal("my-server", mcpSpan.Attributes["mcp.server.name"]);
        Assert.Equal("ping", mcpSpan.Attributes["mcp.method.name"]);
        Assert.Equal("tool", mcpSpan.Attributes["mcp.kind"]);
        Assert.Equal("stop", mcpSpan.Attributes["gen_ai.response.finish_reason"]);

        // LLM usage metrics from MCP response (same as llm.call)
        Assert.Equal("mock-model", mcpSpan.Attributes["gen_ai.request.model"]);
        Assert.Equal(5L, mcpSpan.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(15L, mcpSpan.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(20L, mcpSpan.Attributes["gen_ai.usage.total_tokens"]);
    }

    [Fact]
    public async Task CustomTelemetry_McpCallPrompt_HasPromptAttributes()
    {
        var recording = new RecordingTelemetry();

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("srv", new MockMcpServerConfig
        {
            Prompts = new List<McpPromptInfo> { new() { Name = "summarize" } }
        });

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          kind: prompt
          method: summarize
");
        var engine = new WorkflowEngine
        {
            McpClientFactory = factory,
            Telemetry = recording
        };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        var span = recording.StepSpans.FirstOrDefault(s => s.Name == "call");
        Assert.NotNull(span);

        Assert.Equal("prompt_get", span!.Attributes["gen_ai.operation.name"]);
        Assert.Equal("prompt", span.Attributes["mcp.kind"]);
        Assert.Equal("srv", span.Attributes["mcp.server.name"]);
        Assert.Equal("stop", span.Attributes["gen_ai.response.finish_reason"]);

        // LLM usage metrics from MCP prompt response
        Assert.Equal("mock-model", span.Attributes["gen_ai.request.model"]);
        Assert.Equal(8L, span.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(12L, span.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(20L, span.Attributes["gen_ai.usage.total_tokens"]);
    }

    [Fact]
    public async Task CustomTelemetry_SkippedStep_RecordsSkipped()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: skipped
        type: template.render
        if: ""${false}""
        input:
          engine: mustache
          template: nope
          mode: text
");
        var engine = new WorkflowEngine { Telemetry = recording };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        var stepEnd = recording.Events
            .Where(e => e.Event == "StepEnd")
            .Select(e => e.Info as StepResultInfo)
            .FirstOrDefault();

        Assert.NotNull(stepEnd);
        Assert.Equal(StepStatus.Skipped, stepEnd!.Status);
    }

    [Fact]
    public async Task CustomTelemetry_FailedStep_RecordsFailed()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: fail
        type: mcp.call
        input:
          server: nonexistent
          method: test
");
        var engine = new WorkflowEngine { Telemetry = recording };
        // No McpClientFactory configured → will fail
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);

        var stepEnd = recording.Events
            .Where(e => e.Event == "StepEnd")
            .Select(e => e.Info as StepResultInfo)
            .FirstOrDefault();

        Assert.NotNull(stepEnd);
        Assert.Equal(StepStatus.Failed, stepEnd!.Status);
        Assert.NotNull(stepEnd.ErrorCode);
        Assert.Equal("error", stepEnd.GenAiFinishReason);

        // WorkflowEnd should report failure
        var wfEnd = recording.Events
            .Where(e => e.Event == "WorkflowEnd")
            .Select(e => e.Info as WorkflowResultInfo)
            .FirstOrDefault();

        Assert.NotNull(wfEnd);
        Assert.False(wfEnd!.Success);
    }

    [Fact]
    public async Task CustomTelemetry_MultipleSteps_TracksAll()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: step1
        type: template.render
        input:
          engine: mustache
          template: A
          mode: text
      - id: step2
        type: template.render
        input:
          engine: mustache
          template: B
          mode: text
      - id: step3
        type: template.render
        input:
          engine: mustache
          template: C
          mode: text
");
        var engine = new WorkflowEngine { Telemetry = recording };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        var stepStarts = recording.Events
            .Where(e => e.Event == "StepStart")
            .Select(e => e.Info as StepTelemetryInfo)
            .ToList();

        Assert.Equal(3, stepStarts.Count);
        Assert.Equal("step1", stepStarts[0]!.StepId);
        Assert.Equal("step2", stepStarts[1]!.StepId);
        Assert.Equal("step3", stepStarts[2]!.StepId);

        var stepEnds = recording.Events
            .Where(e => e.Event == "StepEnd")
            .Select(e => e.Info as StepResultInfo)
            .ToList();

        Assert.Equal(3, stepEnds.Count);
        Assert.All(stepEnds, s => Assert.Equal(StepStatus.Succeeded, s!.Status));
    }

    [Fact]
    public async Task CustomTelemetry_NestedSequence_KeepsChildStepsUnderParentStepSpan()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
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

        var engine = new WorkflowEngine { Telemetry = recording };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        var seqSpan = recording.StepSpans.Single(s => s.Name == "seq");
        var aSpan = recording.StepSpans.Single(s => s.Name == "a");
        var bSpan = recording.StepSpans.Single(s => s.Name == "b");

        Assert.Equal("main", seqSpan.ParentName);
        Assert.Equal("seq", aSpan.ParentName);
        Assert.Equal("seq", bSpan.ParentName);
    }

    [Fact]
    public async Task CustomTelemetry_WorkflowEnd_HasDuration()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: s
        type: template.render
        input:
          engine: mustache
          template: ok
          mode: text
");
        var engine = new WorkflowEngine { Telemetry = recording };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        var wfEnd = recording.Events
            .Where(e => e.Event == "WorkflowEnd")
            .Select(e => e.Info as WorkflowResultInfo)
            .Single();

        Assert.True(wfEnd!.Duration > TimeSpan.Zero);
        Assert.True(wfEnd.Success);
    }

    // ────── Telemetry can be replaced at runtime ──────

    [Fact]
    public void Telemetry_CanBeSet()
    {
        var engine = new WorkflowEngine();
        var custom = new RecordingTelemetry();
        engine.Telemetry = custom;
        Assert.Same(custom, engine.Telemetry);
    }

    // ────── LogStepContent emits input/output events ──────

    [Fact]
    public async Task LogStepContent_EmitsInputAndOutputEvents()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: greet
        type: template.render
        input:
          engine: mustache
          template: ""Hello {{name}}""
          data:
            name: World
          mode: text
");
        var engine = new WorkflowEngine
        {
            Telemetry = recording,
            Limits = new ExecutionLimits { LogStepContent = true }
        };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);

        // The step span for "greet" should have input and output events
        var greetSpan = recording.StepSpans.Single(s => s.Name == "greet");
        Assert.Equal(2, greetSpan.SpanEvents.Count);

        Assert.Equal("gnougo-flow.step.input", greetSpan.SpanEvents[0].Name);
        Assert.Contains(greetSpan.SpanEvents[0].Attributes!,
            kv => kv.Key == "gnougo-flow.content.input");
        Assert.Contains(greetSpan.SpanEvents[0].Attributes!,
            kv => kv.Key == "gnougo-flow.step.type" && (string?)kv.Value == "template.render");
        Assert.Contains(greetSpan.SpanEvents[0].Attributes!,
            kv => kv.Key == "gnougo-flow.step.call_depth" && Equals(kv.Value, 0));

        Assert.Equal("gnougo-flow.step.output", greetSpan.SpanEvents[1].Name);
        Assert.Contains(greetSpan.SpanEvents[1].Attributes!,
            kv => kv.Key == "gnougo-flow.content.output");
        Assert.Contains(greetSpan.SpanEvents[1].Attributes!,
            kv => kv.Key == "gnougo-flow.step.type" && (string?)kv.Value == "template.render");
        Assert.Contains(greetSpan.SpanEvents[1].Attributes!,
            kv => kv.Key == "gnougo-flow.step.call_depth" && Equals(kv.Value, 0));
    }

    [Fact]
    public async Task LogStepContent_Disabled_NoEvents()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
dsl: 1
workflows:
  main:
    steps:
      - id: greet
        type: template.render
        input:
          engine: mustache
          template: ""Hello""
          mode: text
");
        var engine = new WorkflowEngine
        {
            Telemetry = recording,
            Limits = new ExecutionLimits { LogStepContent = false }
        };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result.Success);

        // No span events should be emitted
        var greetSpan = recording.StepSpans.Single(s => s.Name == "greet");
        Assert.Empty(greetSpan.SpanEvents);
    }
}

