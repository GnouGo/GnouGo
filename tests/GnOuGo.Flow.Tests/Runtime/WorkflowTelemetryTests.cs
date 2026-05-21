using System.Text.Json.Nodes;
using Moq;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowTelemetryTests
{
    private static double ExpectedCost(string model, long inputTokens, long outputTokens, string? providerType = null)
    {
        var cost = ModelMetadataCatalog.EstimateCost(model, inputTokens, outputTokens, providerType: providerType);
        Assert.True(cost.HasValue, $"Expected pricing metadata for model '{model}' provider '{providerType ?? "<default>"}'.");
        return (double)cost.Value;
    }

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
        public List<TestSpan> ChildSpans { get; } = new();

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

        public ITelemetrySpan SpanStart(ITelemetrySpan parentSpan, TelemetrySpanInfo info)
        {
            Events.Add(("SpanStart", info));
            var span = new TestSpan { Name = info.Name, ParentName = (parentSpan as TestSpan)?.Name };
            if (info.Attributes != null)
            {
                foreach (var kv in info.Attributes)
                    span.Attributes[kv.Key] = kv.Value;
            }
            ChildSpans.Add(span);
            return span;
        }

        public void SpanEnd(ITelemetrySpan span, TelemetrySpanResultInfo result)
        {
            Events.Add(("SpanEnd", result));
            if (span is TestSpan testSpan)
            {
                testSpan.Attributes["gnougo-flow.span.success"] = result.Success;
                if (result.ErrorType != null)
                    testSpan.Attributes["error.type"] = result.ErrorType;
            }
        }
    }

    [Fact]
    public async Task CustomTelemetry_ReceivesWorkflowAndStepEvents()
    {
        var recording = new RecordingTelemetry();

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
        Assert.Equal("yaml", wfInfo.SourceFormat);
        Assert.Contains("type: template.render", wfInfo.SourceText, StringComparison.Ordinal);

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
    public void WorkflowTelemetrySourceFormatter_RedactsSensitiveValuesAndTruncates()
    {
        var source = """
version: 1
api_key: should-not-leak
workflows:
  main:
    steps: []
""";

        var snapshot = WorkflowTelemetrySourceFormatter.Format(source, limit: 40);

        Assert.True(snapshot.Redacted);
        Assert.True(snapshot.Truncated);
        Assert.Equal(source.Length, snapshot.OriginalLength);
        Assert.DoesNotContain("should-not-leak", snapshot.Text, StringComparison.Ordinal);
        Assert.Contains("api_key: <redacted>", snapshot.Text, StringComparison.Ordinal);
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
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:

          model: gpt-5.5
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
        Assert.Equal("gpt-5.5", llmSpan.Attributes["gen_ai.request.model"]);
        Assert.Equal(0.7, llmSpan.Attributes["gen_ai.request.temperature"]);

        // Response attributes (written by executor after the LLM call)
        Assert.Equal("gpt-5.5", llmSpan.Attributes["gen_ai.response.model"]);
        Assert.Equal("stop", llmSpan.Attributes["gen_ai.response.finish_reason"]);
        Assert.Equal(10L, llmSpan.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(20L, llmSpan.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(30, llmSpan.Attributes["gen_ai.usage.total_tokens"]);
        Assert.Equal(ExpectedCost("gpt-5.5", 10, 20, "openai"), Assert.IsType<double>(llmSpan.Attributes["gen_ai.usage.cost"]), precision: 8);
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
 version: 1
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
    public async Task CustomTelemetry_WorkflowPlanMcpServerPrefilter_EmitsGenAiEvents()
    {
        var recording = new RecordingTelemetry();
        var callIndex = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (++callIndex) switch
            {
                1 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"github\",\"reason\":\"repo\"}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"reason\":\"repo\"}]}")!,
                    Usage = JsonNode.Parse("{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}")!
                },
                2 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}")!
                },
                _ => new LLMResponse
                {
                    Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text",
                    Usage = JsonNode.Parse("{\"prompt_tokens\":10,\"completion_tokens\":20,\"total_tokens\":30}")!
                }
            });

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("github", new MockMcpServerConfig
        {
            Description = "GitHub repository automation",
            Tools = new List<McpToolInfo> { new() { Name = "list_repos" } }
        });
        factory.RegisterServer("weather", new MockMcpServerConfig
        {
            Description = "Weather forecasts",
            Tools = new List<McpToolInfo> { new() { Name = "get_weather" } }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            provider: openai
            model: gpt-5.5
            instruction: list repositories
          validate:
            compile: false
");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = factory,
            Telemetry = recording,
            Limits = new ExecutionLimits { LogStepContent = true }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var planSpan = recording.StepSpans.Single(s => s.Name == "plan");
        Assert.Contains(planSpan.SpanEvents, e => e.Name == "gnougo-flow.plan.prefilter.servers.start"
            && e.Attributes != null
            && e.Attributes.Any(kv => kv.Key == "gen_ai.operation.name" && (string?)kv.Value == "chat")
            && e.Attributes.Any(kv => kv.Key == "gen_ai.request.model" && (string?)kv.Value == "gpt-5.5"));
        Assert.Contains(planSpan.SpanEvents, e => e.Name == "gnougo-flow.plan.prefilter.servers.usage"
            && e.Attributes != null
            && e.Attributes.Any(kv => kv.Key == "gen_ai.usage.total_tokens" && (long)kv.Value! == 5L)
            && e.Attributes.Any(kv => kv.Key == "gen_ai.usage.cost" && Math.Abs((double)kv.Value! - ExpectedCost("gpt-5.5", 3, 2, "openai")) < 0.00000001d));
        Assert.Contains(planSpan.SpanEvents, e => e.Name == "gen_ai.content.prompt"
            && e.Attributes != null
            && e.Attributes.Any(kv => kv.Key == "gnougo-flow.plan.phase" && (string?)kv.Value == "mcp_server_prefilter"));
        Assert.Contains(planSpan.SpanEvents, e => e.Name == "gnougo-flow.plan.prefilter.servers.result"
            && e.Attributes != null
            && e.Attributes.Any(kv => kv.Key == "mcp.servers_selected" && (int)kv.Value! == 1));

        var childSpans = recording.ChildSpans.ToDictionary(s => s.Name, StringComparer.Ordinal);
        Assert.Equal("plan", childSpans["workflow.plan.mcp_server_prefilter"].ParentName);
        Assert.Equal("plan", childSpans["workflow.plan.mcp_discovery"].ParentName);
        Assert.Equal("plan", childSpans["workflow.plan.mcp_capability_prefilter"].ParentName);
        Assert.Equal("plan", childSpans["workflow.plan.generate"].ParentName);
        Assert.Equal("plan", childSpans["workflow.plan.validate"].ParentName);
        Assert.Equal("chat", childSpans["workflow.plan.mcp_server_prefilter"].Attributes["gen_ai.operation.name"]);
        Assert.Equal(5L, childSpans["workflow.plan.mcp_server_prefilter"].Attributes["gen_ai.usage.total_tokens"]);
        Assert.Equal(ExpectedCost("gpt-5.5", 3, 2, "openai"), Assert.IsType<double>(childSpans["workflow.plan.mcp_server_prefilter"].Attributes["gen_ai.usage.cost"]), precision: 8);
        Assert.Equal(1, childSpans["workflow.plan.mcp_server_prefilter"].Attributes["mcp.servers_selected"]);
        Assert.Equal(ExpectedCost("gpt-5.5", 10, 20, "openai"), Assert.IsType<double>(childSpans["workflow.plan.generate"].Attributes["gen_ai.usage.cost"]), precision: 8);
        Assert.Equal(ExpectedCost("gpt-5.5", 10, 20, "openai"), Assert.IsType<double>(planSpan.Attributes["gen_ai.usage.cost"]), precision: 8);
    }

    [Fact]
    public async Task CustomTelemetry_McpCall_HasMcpAttributes()
    {
        var recording = new RecordingTelemetry();

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("my-server", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "ping" } },
            ToolHandlers = new()
            {
                ["ping"] = _ => new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["ok"] = true },
                    Model = "gpt-5.5",
                    Usage = new JsonObject
                    {
                        ["prompt_tokens"] = 5,
                        ["completion_tokens"] = 15,
                        ["total_tokens"] = 20
                    }
                }
            }
        });

        var wf = CompileMain(@"
version: 1
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
        Assert.Equal("gpt-5.5", mcpSpan.Attributes["gen_ai.request.model"]);
        Assert.Equal(5L, mcpSpan.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(15L, mcpSpan.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(20L, mcpSpan.Attributes["gen_ai.usage.total_tokens"]);
        Assert.Equal(ExpectedCost("gpt-5.5", 5, 15), Assert.IsType<double>(mcpSpan.Attributes["gen_ai.usage.cost"]), precision: 8);
    }

    [Fact]
    public async Task CustomTelemetry_McpCallPrompt_HasPromptAttributes()
    {
        var recording = new RecordingTelemetry();

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("srv", new MockMcpServerConfig
        {
            Prompts = new List<McpPromptInfo> { new() { Name = "summarize" } },
            PromptHandlers = new()
            {
                ["summarize"] = _ => new McpGetPromptResult
                {
                    Description = "Summary prompt",
                    Messages = new List<McpPromptMessage> { new() { Role = "user", Content = "Summarize" } },
                    Model = "gpt-5.5",
                    Usage = new JsonObject
                    {
                        ["prompt_tokens"] = 8,
                        ["completion_tokens"] = 12,
                        ["total_tokens"] = 20
                    }
                }
            }
        });

        var wf = CompileMain(@"
version: 1
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
        Assert.Equal("gpt-5.5", span.Attributes["gen_ai.request.model"]);
        Assert.Equal(8L, span.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(12L, span.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(20L, span.Attributes["gen_ai.usage.total_tokens"]);
        Assert.Equal(ExpectedCost("gpt-5.5", 8, 12), Assert.IsType<double>(span.Attributes["gen_ai.usage.cost"]), precision: 8);
    }

    [Fact]
    public async Task CustomTelemetry_McpCallLlmAssisted_AccumulatesSelectionAndFinalizeCost()
    {
        var recording = new RecordingTelemetry();

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("browser");
        mockSession.Setup(s => s.CallToolAsync("browser_get_content", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject { ["content"] = "<nav><a href=\"/docs\">Docs</a></nav>" }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("browser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var callIndex = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (++callIndex) switch
            {
                1 => new LLMResponse
                {
                    Text = "I will inspect rendered HTML.",
                    ToolCalls = new List<LLMToolCall>
                    {
                        new()
                        {
                            Name = "browser_get_content",
                            Arguments = new JsonObject { ["url"] = "https://example.test", ["format"] = "html" }
                        }
                    },
                    Usage = new JsonObject
                    {
                        ["prompt_tokens"] = 10,
                        ["completion_tokens"] = 5,
                        ["total_tokens"] = 15
                    }
                },
                _ => new LLMResponse
                {
                    Text = "{\"links\":[{\"title\":\"Docs\",\"url\":\"https://example.test/docs\"}]}",
                    Json = JsonNode.Parse("{\"links\":[{\"title\":\"Docs\",\"url\":\"https://example.test/docs\"}]}")!,
                    Usage = new JsonObject
                    {
                        ["prompt_tokens"] = 20,
                        ["completion_tokens"] = 8,
                        ["total_tokens"] = 28
                    }
                }
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: scrape
        type: mcp.call
        input:
          server: browser
          provider: openai
          model: gpt-5.5
          prompt: Extract navigation links from https://example.test.
          tools:
            - name: browser_get_content
              description: Read rendered page content.
              input_schema:
                type: object
                properties:
                  url: { type: string }
                  format: { type: string }
          structured_output:
            schema_inline:
              type: object
              properties:
                links:
                  type: array
                  items:
                    type: object
                    properties:
                      title: { type: string }
                      url: { type: string }
                    required: [title, url]
              required: [links]
");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mockFactory.Object,
            Telemetry = recording
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var span = recording.StepSpans.Single(s => s.Name == "scrape");
        Assert.Equal("gpt-5.5", span.Attributes["gen_ai.request.model"]);
        Assert.Equal(30L, span.Attributes["gen_ai.usage.input_tokens"]);
        Assert.Equal(13L, span.Attributes["gen_ai.usage.output_tokens"]);
        Assert.Equal(43L, span.Attributes["gen_ai.usage.total_tokens"]);
        Assert.Equal(ExpectedCost("gpt-5.5", 30, 13, "openai"), Assert.IsType<double>(span.Attributes["gen_ai.usage.cost"]), precision: 8);
    }

    [Fact]
    public async Task CustomTelemetry_SkippedStep_RecordsSkipped()
    {
        var recording = new RecordingTelemetry();

        var wf = CompileMain(@"
version: 1
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
version: 1
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
version: 1
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
version: 1
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
version: 1
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

