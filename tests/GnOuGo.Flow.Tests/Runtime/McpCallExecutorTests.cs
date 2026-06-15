using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class McpCallExecutorTests
{
    private static CompiledDocument CompileDoc(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        return new WorkflowCompiler().Compile(doc);
    }

    private static async Task<RunResult> RunMain(string yaml, JsonObject? inputs = null,
        IMcpClientFactory? mcpFactory = null, ILLMClient? llm = null, IWorkflowTelemetry? telemetry = null)
    {
        var compiled = CompileDoc(yaml);
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            McpClientFactory = mcpFactory,
            LLMClient = llm,
            Telemetry = telemetry ?? NullWorkflowTelemetry.Instance
        };
        return await engine.ExecuteAsync(wf, inputs ?? new JsonObject(), CancellationToken.None);
    }

    // ------ Basic mcp.call ------

    [Fact]
    public async Task McpCall_BasicCall_ReturnsOk()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("test-server");
        mockSession.Setup(s => s.CallToolAsync("greet", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject { ["message"] = "Hello World" }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("test-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call_mcp
        type: mcp.call
        input:
          server: test-server
          method: greet
          request:
            name: World
    outputs:
      status: "${data.steps.call_mcp.status}"
      msg: "${data.steps.call_mcp.response.message}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());
        Assert.Equal("Hello World", result.Outputs["msg"]!.GetValue<string>());

        mockSession.Verify(s => s.CallToolAsync("greet", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpCall_ToolProgressEvents_AreForwardedAsThinkingTelemetry()
    {
        var spanEvents = new List<(string Name, IReadOnlyList<KeyValuePair<string, object?>>? Attributes)>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        stepSpan
            .Setup(s => s.AddEvent(It.IsAny<string>(), It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback<string, IReadOnlyList<KeyValuePair<string, object?>>?>((name, attributes) => spanEvents.Add((name, attributes)));

        var telemetry = new Mock<IWorkflowTelemetry>();
        telemetry.Setup(t => t.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>())).Returns(workflowSpan.Object);
        telemetry.Setup(t => t.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>())).Returns(stepSpan.Object);

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("code");
        mockSession.Setup(s => s.CallToolAsync("code_agent_edit", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject
                {
                    ["summary"] = "done",
                    ["progressEvents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "session_create",
                            ["level"] = "thinking",
                            ["message"] = "Creating Copilot agent session.",
                            ["timestamp"] = "2026-05-19T00:00:00Z"
                        },
                        new JsonObject
                        {
                            ["kind"] = "file_modified",
                            ["level"] = "info",
                            ["message"] = "Modified src/Program.cs.",
                            ["file"] = "src/Program.cs"
                        }
                    }
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("code", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: edit
        type: mcp.call
        input:
          server: code
          method: code_agent_edit
          request:
            task: Update the program.
""", mcpFactory: mockFactory.Object, telemetry: telemetry.Object);

        Assert.True(result.Success);
        var thinkingEvents = spanEvents
            .Where(e => e.Name == "gnougo-flow.step.thinking")
            .Select(e => e.Attributes?.ToDictionary(kv => kv.Key, kv => kv.Value))
            .Where(attrs => attrs != null)
            .Cast<Dictionary<string, object?>>()
            .ToArray();

        Assert.Contains(thinkingEvents, attrs =>
            string.Equals(attrs["gnougo-flow.thinking.message"]?.ToString(), "Creating Copilot agent session.", StringComparison.Ordinal) &&
            string.Equals(attrs["gnougo-flow.thinking.source"]?.ToString(), "mcp.progress", StringComparison.Ordinal) &&
            string.Equals(attrs["mcp.server.name"]?.ToString(), "code", StringComparison.Ordinal) &&
            string.Equals(attrs["mcp.method.name"]?.ToString(), "code_agent_edit", StringComparison.Ordinal));
        Assert.Contains(thinkingEvents, attrs =>
            string.Equals(attrs["gnougo-flow.thinking.message"]?.ToString(), "Modified src/Program.cs.", StringComparison.Ordinal) &&
            string.Equals(attrs["gnougo-flow.thinking.level"]?.ToString(), "info", StringComparison.Ordinal) &&
            string.Equals(attrs["gnougo-flow.thinking.file"]?.ToString(), "src/Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task McpCall_RealtimeProgressEvents_AreForwardedBeforeToolReturns()
    {
        var spanEvents = new List<(string Name, IReadOnlyList<KeyValuePair<string, object?>>? Attributes)>();
        var realtimeEventObservedBeforeReturn = false;
        var callReturned = false;
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        stepSpan
            .Setup(s => s.AddEvent(It.IsAny<string>(), It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback<string, IReadOnlyList<KeyValuePair<string, object?>>?>((name, attributes) =>
            {
                spanEvents.Add((name, attributes));
                var source = attributes?.FirstOrDefault(kv => kv.Key == "gnougo-flow.thinking.source").Value?.ToString();
                if (name == "gnougo-flow.step.thinking" && source == "mcp.realtime_progress" && !callReturned)
                    realtimeEventObservedBeforeReturn = true;
            });

        var telemetry = new Mock<IWorkflowTelemetry>();
        telemetry.Setup(t => t.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>())).Returns(workflowSpan.Object);
        telemetry.Setup(t => t.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>())).Returns(stepSpan.Object);

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("code");
        mockSession.Setup(s => s.CallToolAsync("code_agent_edit", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                ConfiguredMcpClientFactory.PublishProgress(new McpRealtimeProgressEvent
                {
                    ServerName = "code",
                    MethodName = "code_agent_edit",
                    Kind = "tool",
                    EventKind = "request_send",
                    Level = "thinking",
                    Message = "Sending agent edit request to Copilot."
                });
                await Task.Delay(20);
                callReturned = true;
                return new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject
                    {
                        ["summary"] = "done",
                        ["progressEvents"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["kind"] = "request_send",
                                ["level"] = "thinking",
                                ["message"] = "Sending agent edit request to Copilot."
                            }
                        }
                    }
                };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("code", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: edit
        type: mcp.call
        input:
          server: code
          method: code_agent_edit
          request:
            task: Update the program.
""", mcpFactory: mockFactory.Object, telemetry: telemetry.Object);

        Assert.True(result.Success);
        Assert.True(realtimeEventObservedBeforeReturn);
        Assert.Equal(1, spanEvents.Count(e =>
            e.Name == "gnougo-flow.step.thinking" &&
            string.Equals(e.Attributes?.FirstOrDefault(kv => kv.Key == "gnougo-flow.thinking.message").Value?.ToString(), "Sending agent edit request to Copilot.", StringComparison.Ordinal)));
        Assert.Contains(spanEvents, e =>
            e.Name == "gnougo-flow.step.thinking" &&
            string.Equals(e.Attributes?.FirstOrDefault(kv => kv.Key == "gnougo-flow.thinking.message").Value?.ToString(), "Sending agent edit request to Copilot.", StringComparison.Ordinal) &&
            string.Equals(e.Attributes?.FirstOrDefault(kv => kv.Key == "gnougo-flow.thinking.source").Value?.ToString(), "mcp.realtime_progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task McpCall_WithExpressions_ResolvesInput()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("my-server");
        mockSession.Setup(s => s.CallToolAsync("search", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject { ["results"] = new JsonArray(JsonValue.Create("result1")) }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("my-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: search
        type: mcp.call
        input:
          server: my-server
          method: search
          request:
            query: "${data.inputs.query}"
    outputs:
      status: "${data.steps.search.status}"
""", inputs: new JsonObject { ["query"] = "test query" }, mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        // Verify the resolved argument was passed
        mockSession.Verify(s => s.CallToolAsync("search",
            It.Is<JsonNode?>(n => n != null && n["query"]!.GetValue<string>() == "test query"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpCall_ToolReturnsError_StatusIsError()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = true,
                Content = new JsonObject { ["error"] = "not found" }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: lookup
          request: {}
    outputs:
      status: "${data.steps.call.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("error", result.Outputs!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_RaiseOnError_TriggersOnErrorWithMcpDetails()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("lookup", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = true,
                Content = new JsonObject
                {
                    ["error_code"] = "NOT_FOUND",
                    ["error_message"] = "Thing was not found."
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: lookup
          request: {}
          raise_on_error: true
        on_error:
          cases:
            - if: '${error.code == "MCP_CALL_ERROR"}'
              action: continue
              set_output:
                recovered: true
                code: "${error.code}"
                mcp_code: "${error.details.mcp_error_code}"
                mcp_message: "${error.details.mcp_error_message}"
    outputs:
      recovered: "${data.steps.call.recovered}"
      code: "${data.steps.call.code}"
      mcp_code: "${data.steps.call.mcp_code}"
      mcp_message: "${data.steps.call.mcp_message}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.True(result.Outputs!["recovered"]!.GetValue<bool>());
        Assert.Equal("MCP_CALL_ERROR", result.Outputs["code"]!.GetValue<string>());
        Assert.Equal("NOT_FOUND", result.Outputs["mcp_code"]!.GetValue<string>());
        Assert.Equal("Thing was not found.", result.Outputs["mcp_message"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_RaiseOnError_DetectsStructuredFailureEnvelopeWhenTransportDoesNotSetIsError()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("lookup", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject
                {
                    ["success"] = false,
                    ["error_code"] = "ALREADY_EXISTS",
                    ["error_message"] = "Already exists."
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: lookup
          request: {}
          raise_on_error: true
        on_error:
          cases:
            - action: continue
              set_output:
                recovered: true
                mcp_code: "${error.details.mcp_error_code}"
    outputs:
      recovered: "${data.steps.call.recovered}"
      mcp_code: "${data.steps.call.mcp_code}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.True(result.Outputs!["recovered"]!.GetValue<bool>());
        Assert.Equal("ALREADY_EXISTS", result.Outputs["mcp_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_WithRequestTemplate_RendersAndParsesJson()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("query", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject { ["ok"] = true } });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: query
          request_template: '{"q": "{{q}}"}'
          template_data:
            q: hello
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        mockSession.Verify(s => s.CallToolAsync("query",
            It.Is<JsonNode?>(n => n != null && n["q"]!.GetValue<string>() == "hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpCall_NoRequest_PassesNull()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("ping", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject { ["pong"] = true } });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: ping
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task McpCall_ServerCallTimeoutOverridesShortGeneratedTimeout()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("slow");
        mockSession.Setup(s => s.CallToolAsync("wait", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, JsonNode? _, CancellationToken ct) =>
            {
                await Task.Delay(50, ct);
                return new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["ok"] = true }
                };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.ServerMetadata).Returns(new List<McpServerMetadata>
        {
            new() { Name = "slow", CallTimeoutSeconds = 5 }
        }.AsReadOnly());
        mockFactory.Setup(f => f.GetClientAsync("slow", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: slow
          method: wait
          timeout_ms: 1
    outputs:
      status: "${data.steps.call.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());
    }


    [Fact]
    public async Task McpCall_RequestCanCarryModelAndTemperature_WhenServerSchemaSupportsThem()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("answer_question", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject { ["ok"] = true }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call_mcp
        type: mcp.call
        input:
          server: srv
          method: answer_question
          request:
            question: Summarize my repo activity
            model: gpt-4.1-mini
            temperature: 0.2
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        mockSession.Verify(s => s.CallToolAsync(
            "answer_question",
            It.Is<JsonNode?>(n => n != null
                && n["question"]!.GetValue<string>() == "Summarize my repo activity"
                && n["model"]!.GetValue<string>() == "gpt-4.1-mini"
                && Math.Abs(n["temperature"]!.GetValue<double>() - 0.2) < 0.0001),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
    // ------ Error handling ------

    [Fact]
    public async Task McpCall_NoFactory_Fails()
    {
        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: any
          method: any
""", mcpFactory: null);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpConnectionError, result.Error!.Code);
    }

    [Fact]
    public async Task McpCall_MissingServer_Fails()
    {
        var mockFactory = new Mock<IMcpClientFactory>();

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          method: test
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
    }

    [Fact]
    public async Task McpCall_NoMethodOrMethods_AutoDiscoversTools()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("srv", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping" },
                new() { Name = "echo", Description = "Echo" }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
    outputs:
      status: "${data.steps.call.status}"
""", mcpFactory: factory);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.NotNull(results);
        Assert.Equal(2, results!.Count);
        Assert.Equal("ping", results[0]!["method"]!.GetValue<string>());
        Assert.Equal("echo", results[1]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_ServerNotFound_Fails()
    {
        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("unknown", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException(
                ErrorCodes.McpServerNotFound, "Server 'unknown' not found"));

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: unknown
          method: test
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpServerNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task McpCall_ConnectionError_WrapsException()
    {
        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection refused"));

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: test
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpCallError, result.Error!.Code);
        Assert.Contains("Connection refused", result.Error.Message);
    }

    [Fact]
    public async Task McpCall_InvalidRequestTemplate_Fails()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: test
          request_template: "not valid json {{"
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
    }

    // ------ InMemoryMcpClientFactory ------

    [Fact]
    public async Task InMemoryFactory_RegisteredServer_ReturnsSession()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("test", new MockMcpServerConfig
        {
            Description = "Test server",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping" }
            }
        });

        var metadata = Assert.Single(factory.ServerMetadata);
        Assert.Equal("test", metadata.Name);
        Assert.Equal("Test server", metadata.Description);
        await using var session = await factory.GetClientAsync("test", CancellationToken.None);
        Assert.Equal("test", session.ServerName);

        var tools = await session.ListToolsAsync(CancellationToken.None);
        Assert.Single(tools);
        Assert.Equal("ping", tools[0].Name);
    }

    [Fact]
    public async Task InMemoryFactory_UnknownServer_Throws()
    {
        var factory = new InMemoryMcpClientFactory();

        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(
            () => factory.GetClientAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public async Task InMemorySession_CustomHandler_ReturnsResult()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("calc", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "add" } },
            ToolHandlers = new()
            {
                ["add"] = args =>
                {
                    var a = args?["a"]?.GetValue<int>() ?? 0;
                    var b = args?["b"]?.GetValue<int>() ?? 0;
                    return new McpCallResult
                    {
                        IsError = false,
                        Content = new JsonObject { ["result"] = a + b }
                    };
                }
            }
        });

        await using var session = await factory.GetClientAsync("calc", CancellationToken.None);
        var result = await session.CallToolAsync("add",
            new JsonObject { ["a"] = 3, ["b"] = 4 },
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(7, result.Content!["result"]!.GetValue<int>());
    }

    [Fact]
    public async Task InMemorySession_DefaultHandler_ReturnsMockResponse()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("srv", new MockMcpServerConfig());

        await using var session = await factory.GetClientAsync("srv", CancellationToken.None);
        var result = await session.CallToolAsync("anything", null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(result.Content!["mock"]!.GetValue<bool>());
    }

    [Fact]
    public async Task InMemoryFactory_Cancellation_Throws()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("srv", new MockMcpServerConfig());

        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => factory.GetClientAsync("srv", cts.Token));
    }

    // ------ Integration: mcp.call with InMemory factory ------

    [Fact]
    public async Task McpCall_Integration_WithInMemoryFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("weather", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Get weather" }
            },
            ToolHandlers = new()
            {
                ["get_weather"] = args =>
                {
                    var city = args?["city"]?.GetValue<string>() ?? "unknown";
                    return new McpCallResult
                    {
                        IsError = false,
                        Content = new JsonObject { ["temp"] = 22, ["city"] = city, ["unit"] = "C" }
                    };
                }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: weather
        type: mcp.call
        input:
          server: weather
          method: get_weather
          request:
            city: "${data.inputs.city}"
    outputs:
      status: "${data.steps.weather.status}"
      temp: "${data.steps.weather.response.temp}"
      city: "${data.steps.weather.response.city}"
""", inputs: new JsonObject { ["city"] = "Paris" }, mcpFactory: factory);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());
        Assert.Equal(22, result.Outputs["temp"]!.GetValue<int>());
        Assert.Equal("Paris", result.Outputs["city"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_Pipeline_McpThenLlm()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("data-server", new MockMcpServerConfig
        {
            ToolHandlers = new()
            {
                ["fetch_data"] = _ => new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["data"] = "important context info" }
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "AI analysis based on context" });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: fetch
        type: mcp.call
        input:
          server: data-server
          method: fetch_data
          request: {}
      - id: analyze
        type: llm.call
        input:
          model: gpt-4
          prompt: "Analyze: ${data.steps.fetch.response.data}"
    outputs:
      mcp_status: "${data.steps.fetch.status}"
      llm_result: "${data.steps.analyze.text}"
""", mcpFactory: factory, llm: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["mcp_status"]!.GetValue<string>());
        Assert.Equal("AI analysis based on context", result.Outputs["llm_result"]!.GetValue<string>());

        mockLlm.Verify(l => l.CallAsync(
            It.Is<LLMRequest>(r => r.Prompt.Contains("important context info")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ------ Validation ------

    [Fact]
    public void Validate_McpCallStepType_IsKnown()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: test
          method: test
""";
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var errors = compiler.Validate(doc);
        Assert.DoesNotContain(errors, e => e.Code == ErrorCodes.StepTypeUnknown);
    }

    // ------ LLMRequest Tools property ------

    [Fact]
    public void LLMRequest_Tools_CanBeSet()
    {
        var req = new LLMRequest
        {
            Model = "gpt-4",
            Prompt = "test",
            Tools = new List<LLMTool>
            {
                new() { Name = "search", Description = "Search the web", InputSchema = new JsonObject { ["type"] = "object" } }
            }
        };

        Assert.NotNull(req.Tools);
        Assert.Single(req.Tools);
        Assert.Equal("search", req.Tools[0].Name);
    }

    [Fact]
    public void LLMResponse_ToolCalls_CanBeSet()
    {
        var resp = new LLMResponse
        {
            Text = "",
            ToolCalls = new List<LLMToolCall>
            {
                new() { Id = "call_1", Name = "search", Arguments = new JsonObject { ["q"] = "hello" } }
            }
        };

        Assert.NotNull(resp.ToolCalls);
        Assert.Single(resp.ToolCalls);
        Assert.Equal("search", resp.ToolCalls[0].Name);
    }

    // ------ LLM with MCP tools injection ------

    [Fact]
    public async Task LlmCall_WithMcpTools_PassesToolsToLLM()
    {
        // This test demonstrates the pattern: first list MCP tools, then pass them to LLM
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("tools-srv", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "calculator", Description = "Do math", InputSchema = new JsonObject { ["type"] = "object" } }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = "The answer is 42" });

        // In a real workflow, you'd use mcp.call to list tools, then pass them to llm.call
        // For now we test that the LLMRequest can carry tools
        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: llm.call
        input:
          model: gpt-4
          prompt: "What is 6 * 7?"
    outputs:
      answer: "${data.steps.ask.text}"
""", mcpFactory: factory, llm: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("The answer is 42", result.Outputs!["answer"]!.GetValue<string>());
    }

    // ------ kind: prompt ------

    [Fact]
    public async Task McpCall_KindPrompt_CallsGetPromptAsync()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.GetPromptAsync("summarize", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpGetPromptResult
            {
                Description = "Summarize text",
                Messages = new List<McpPromptMessage>
                {
                    new() { Role = "user", Content = "Please summarize: Hello world" }
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call_prompt
        type: mcp.call
        input:
          server: srv
          kind: prompt
          method: summarize
          request:
            text: "Hello world"
    outputs:
      status: "${data.steps.call_prompt.status}"
      text: "${data.steps.call_prompt.text}"
      desc: "${data.steps.call_prompt.description}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());
        Assert.Contains("summarize", result.Outputs["text"]!.GetValue<string>());
        Assert.Equal("Summarize text", result.Outputs["desc"]!.GetValue<string>());

        var stepOut = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(stepOut);
        var messages = stepOut["messages"] as JsonArray;
        Assert.NotNull(messages);
        Assert.Single(messages!);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Contains("Hello world", messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_KindTool_DefaultBehavior()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("ping", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject { ["pong"] = true } });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        // Without kind (default = tool)
        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          method: ping
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        var stepOut = result.StepResults[0].Output as JsonObject;
        Assert.Equal("ok", stepOut!["status"]!.GetValue<string>());
        Assert.True(stepOut["response"]!["pong"]!.GetValue<bool>());

        // GetPromptAsync should NOT have been called
        mockSession.Verify(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpCall_KindExplicitTool_CallsCallToolAsync()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("ping", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject { ["ok"] = true } });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          kind: tool
          method: ping
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        mockSession.Verify(s => s.CallToolAsync("ping", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Once);
        mockSession.Verify(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpCall_KindInvalid_Fails()
    {
        var mockFactory = new Mock<IMcpClientFactory>();

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          kind: invalid
          method: test
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("kind", result.Error.Message);
    }

    [Fact]
    public async Task McpCall_KindPrompt_ErrorWrapsAsMcpPromptError()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.GetPromptAsync("bad", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("prompt failed"));
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: srv
          kind: prompt
          method: bad
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpPromptError, result.Error!.Code);
        Assert.Contains("prompt failed", result.Error.Message);
    }

    // ------ InMemorySession GetPromptAsync ------

    [Fact]
    public async Task InMemorySession_PromptHandler_ReturnsCustomResult()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("test", new MockMcpServerConfig
        {
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "greet", Description = "Greeting" }
            },
            PromptHandlers = new()
            {
                ["greet"] = args => new McpGetPromptResult
                {
                    Description = "Custom greeting",
                    Messages = new List<McpPromptMessage>
                    {
                        new() { Role = "user", Content = $"Hello {args?["name"]?.GetValue<string>() ?? "world"}!" }
                    }
                }
            }
        });

        await using var session = await factory.GetClientAsync("test", CancellationToken.None);
        var result = await session.GetPromptAsync("greet", new JsonObject { ["name"] = "Alice" }, CancellationToken.None);

        Assert.Equal("Custom greeting", result.Description);
        Assert.Single(result.Messages);
        Assert.Equal("Hello Alice!", result.Messages[0].Content);
    }

    [Fact]
    public async Task InMemorySession_DefaultPromptHandler_ReturnsMock()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("test", new MockMcpServerConfig());

        await using var session = await factory.GetClientAsync("test", CancellationToken.None);
        var result = await session.GetPromptAsync("unknown_prompt", null, CancellationToken.None);

        Assert.NotNull(result.Description);
        Assert.Single(result.Messages);
        Assert.Contains("unknown_prompt", result.Messages[0].Content);
    }

    // ------ List-then-Call pattern with prompts ------

    [Fact]
    public async Task McpListThenCall_LoopOverPrompts_WithKindPrompt()
    {
        int listPromptsCalls = 0;
        int getPromptCalls = 0;

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>().AsReadOnly());
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref listPromptsCalls);
                return new List<McpPromptInfo>
                {
                    new() { Name = "summarize", Description = "Summarize" },
                    new() { Name = "translate", Description = "Translate" }
                }.AsReadOnly();
            });
        mockSession.Setup(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, JsonNode? args, CancellationToken _) =>
            {
                Interlocked.Increment(ref getPromptCalls);
                return new McpGetPromptResult
                {
                    Description = $"Prompt: {name}",
                    Messages = new List<McpPromptMessage>
                    {
                        new() { Role = "user", Content = $"Resolved prompt '{name}'" }
                    }
                };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: discover
        type: mcp.list
        input:
          servers: [srv]
          include:
            - tools
            - prompts

      - id: call_prompts
        type: loop.parallel
        input:
          items: "${data.steps.discover.prompts}"
        item_var: p
        steps:
          - id: invoke
            type: mcp.call
            input:
              server: srv
              kind: prompt
              method: "${data.p.name}"
              request:
                text: "test input"

    outputs:
      prompt_count: "${len(data.steps.discover.prompts)}"
      calls_made: "${data.steps.call_prompts.count}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        // ListPromptsAsync called exactly once (by mcp.list)
        Assert.Equal(1, listPromptsCalls);

        // GetPromptAsync called twice (once per discovered prompt)
        Assert.Equal(2, getPromptCalls);

        Assert.Equal(2, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs!["prompt_count"]));
        Assert.Equal(2, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs["calls_made"]));
    }

    [Fact]
    public async Task McpListThenCall_MixedToolsAndPrompts_EndToEnd()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("demo", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping" }
            },
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "greet", Description = "Greeting" }
            },
            ToolHandlers = new()
            {
                ["ping"] = _ => new McpCallResult { IsError = false, Content = new JsonObject { ["pong"] = true } }
            },
            PromptHandlers = new()
            {
                ["greet"] = args => new McpGetPromptResult
                {
                    Description = "Greeting prompt",
                    Messages = new List<McpPromptMessage>
                    {
                        new() { Role = "user", Content = "Hello!" }
                    }
                }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: discover
        type: mcp.list
        input:
          servers: [demo]
          include:
            - tools
            - prompts

      - id: call_tools
        type: loop.parallel
        input:
          items: "${data.steps.discover.tools}"
        item_var: t
        steps:
          - id: invoke_tool
            type: mcp.call
            input:
              server: demo
              kind: tool
              method: "${data.t.name}"

      - id: call_prompts
        type: loop.parallel
        input:
          items: "${data.steps.discover.prompts}"
        item_var: p
        steps:
          - id: invoke_prompt
            type: mcp.call
            input:
              server: demo
              kind: prompt
              method: "${data.p.name}"

    outputs:
      tool_count: "${len(data.steps.discover.tools)}"
      prompt_count: "${len(data.steps.discover.prompts)}"
      tools_called: "${data.steps.call_tools.count}"
      prompts_called: "${data.steps.call_prompts.count}"
""", mcpFactory: factory);

        Assert.True(result.Success);
        Assert.Equal(1, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs!["tool_count"]));
        Assert.Equal(1, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs["prompt_count"]));
        Assert.Equal(1, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs["tools_called"]));
        Assert.Equal(1, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs["prompts_called"]));
    }

    // ------ Batch mode: methods[] ------

    [Fact]
    public async Task McpCall_BatchTools_CallsEachMethod()
    {
        var calledTools = new List<string>();

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, JsonNode? args, CancellationToken _) =>
            {
                lock (calledTools) calledTools.Add(name);
                return new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["tool"] = name, ["ok"] = true }
                };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: batch
        type: mcp.call
        input:
          server: srv
          methods:
            - ping
            - echo
            - search
          request:
            source: test
    outputs:
      status: "${data.steps.batch.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        // Verify all 3 tools were called
        Assert.Equal(3, calledTools.Count);
        Assert.Contains("ping", calledTools);
        Assert.Contains("echo", calledTools);
        Assert.Contains("search", calledTools);

        // Verify results array
        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.NotNull(results);
        Assert.Equal(3, results!.Count);
        Assert.Equal("ping", results[0]!["method"]!.GetValue<string>());
        Assert.Equal("echo", results[1]!["method"]!.GetValue<string>());
        Assert.Equal("search", results[2]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_BatchPrompts_CallsGetPromptForEach()
    {
        var calledPrompts = new List<string>();

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, JsonNode? args, CancellationToken _) =>
            {
                lock (calledPrompts) calledPrompts.Add(name);
                return new McpGetPromptResult
                {
                    Description = $"Prompt {name}",
                    Messages = new List<McpPromptMessage>
                    {
                        new() { Role = "user", Content = $"Resolved {name}" }
                    }
                };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: batch
        type: mcp.call
        input:
          server: srv
          kind: prompt
          methods:
            - summarize
            - translate
          request:
            text: "hello"
    outputs:
      status: "${data.steps.batch.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        Assert.Equal(2, calledPrompts.Count);
        Assert.Contains("summarize", calledPrompts);
        Assert.Contains("translate", calledPrompts);

        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.Equal(2, results!.Count);
        Assert.Equal("summarize", results[0]!["method"]!.GetValue<string>());
        Assert.Contains("Resolved summarize", results[0]!["text"]!.GetValue<string>());
        Assert.Equal("translate", results[1]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_BatchWithToolError_StatusIsError()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("good", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject { ["ok"] = true } });
        mockSession.Setup(s => s.CallToolAsync("bad", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = true, Content = new JsonObject { ["error"] = "fail" } });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: batch
        type: mcp.call
        input:
          server: srv
          methods:
            - good
            - bad
    outputs:
      status: "${data.steps.batch.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success); // workflow succeeds, batch status reports error
        Assert.Equal("error", result.Outputs!["status"]!.GetValue<string>());

        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.Equal("ok", results![0]!["status"]!.GetValue<string>());
        Assert.Equal("error", results[1]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_BatchEmptyMethods_Fails()
    {
        var mockFactory = new Mock<IMcpClientFactory>();

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: batch
        type: mcp.call
        input:
          server: srv
          methods: []
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("methods", result.Error.Message);
    }

    [Fact]
    public async Task McpCall_BatchSingleMethodStillReturnsBatch()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.CallToolAsync("ping", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject { ["pong"] = true } });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: batch
        type: mcp.call
        input:
          server: srv
          methods:
            - ping
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        var stepOut = result.StepResults[0].Output as JsonObject;
        // Even with one method, batch mode returns results array
        Assert.NotNull(stepOut!["results"]);
        var results = stepOut["results"] as JsonArray;
        Assert.Single(results!);
        Assert.Equal("ping", results[0]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_BatchTools_InMemoryFactory_EndToEnd()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("demo", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Get weather" },
                new() { Name = "search", Description = "Search" }
            },
            ToolHandlers = new()
            {
                ["get_weather"] = args => new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["temp"] = 22 }
                },
                ["search"] = args => new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["results"] = 5 }
                }
            },
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "summarize", Description = "Summarize" }
            },
            PromptHandlers = new()
            {
                ["summarize"] = args => new McpGetPromptResult
                {
                    Description = "Summary",
                    Messages = new List<McpPromptMessage>
                    {
                        new() { Role = "user", Content = "Please summarize" }
                    }
                }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: discover
        type: mcp.list
        input:
          servers: [demo]
          include:
            - tools
            - prompts

      - id: batch_tools
        type: mcp.call
        input:
          server: demo
          methods:
            - get_weather
            - search

      - id: batch_prompts
        type: mcp.call
        input:
          server: demo
          kind: prompt
          methods:
            - summarize

    outputs:
      tool_count: "${len(data.steps.discover.tools)}"
      prompt_count: "${len(data.steps.discover.prompts)}"
      tools_status: "${data.steps.batch_tools.status}"
      prompts_status: "${data.steps.batch_prompts.status}"
""", mcpFactory: factory);

        Assert.True(result.Success);
        Assert.Equal(2, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs!["tool_count"]));
        Assert.Equal(1, (int)GnOuGo.Flow.Core.Expressions.ExpressionEvaluator.GetNumber(result.Outputs["prompt_count"]));
        Assert.Equal("ok", result.Outputs["tools_status"]!.GetValue<string>());
        Assert.Equal("ok", result.Outputs["prompts_status"]!.GetValue<string>());
    }

    // ------ Auto-discover mode ------

    [Fact]
    public async Task McpCall_AutoDiscover_KindTool_CallsAllTools()
    {
        int listToolsCalls = 0;
        int callToolCalls = 0;

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref listToolsCalls);
                return new List<McpToolInfo>
                {
                    new() { Name = "alpha", Description = "A" },
                    new() { Name = "beta", Description = "B" },
                    new() { Name = "gamma", Description = "C" }
                }.AsReadOnly();
            });
        mockSession.Setup(s => s.CallToolAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, JsonNode? _, CancellationToken _) =>
            {
                Interlocked.Increment(ref callToolCalls);
                return new McpCallResult { IsError = false, Content = new JsonObject { ["ok"] = name } };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: auto
        type: mcp.call
        input:
          server: srv
    outputs:
      status: "${data.steps.auto.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());
        Assert.Equal(1, listToolsCalls);
        Assert.Equal(3, callToolCalls);

        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.Equal(3, results!.Count);
        Assert.Equal("alpha", results[0]!["method"]!.GetValue<string>());
        Assert.Equal("beta", results[1]!["method"]!.GetValue<string>());
        Assert.Equal("gamma", results[2]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_AutoDiscover_KindPrompt_CallsAllPrompts()
    {
        int listPromptsCalls = 0;
        int getPromptCalls = 0;

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref listPromptsCalls);
                return new List<McpPromptInfo>
                {
                    new() { Name = "summarize", Description = "Sum" },
                    new() { Name = "translate", Description = "Trans" }
                }.AsReadOnly();
            });
        mockSession.Setup(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, JsonNode? _, CancellationToken _) =>
            {
                Interlocked.Increment(ref getPromptCalls);
                return new McpGetPromptResult
                {
                    Description = name,
                    Messages = new List<McpPromptMessage> { new() { Role = "user", Content = name } }
                };
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: auto
        type: mcp.call
        input:
          server: srv
          kind: prompt
          request:
            text: hello
    outputs:
      status: "${data.steps.auto.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());
        Assert.Equal(1, listPromptsCalls);
        Assert.Equal(2, getPromptCalls);

        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.Equal(2, results!.Count);
        Assert.Equal("summarize", results[0]!["method"]!.GetValue<string>());
        Assert.Equal("translate", results[1]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_AutoDiscover_KindPrompt_UnsupportedPromptsList_ReturnsEmptyResults()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Method 'prompts/list' is not available."));
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: auto
        type: mcp.call
        input:
          server: srv
          kind: prompt
          request:
            text: hello
    outputs:
      status: "${data.steps.auto.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        var stepOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        var results = Assert.IsType<JsonArray>(stepOut["results"]);
        Assert.Empty(results);
        mockSession.Verify(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpCall_AutoDiscover_KindPrompt_RealPromptsListError_Fails()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection failed"));
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: auto
        type: mcp.call
        input:
          server: srv
          kind: prompt
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpPromptError, result.Error!.Code);
        Assert.Contains("connection failed", result.Error.Message);
    }

    [Fact]
    public async Task McpCall_AutoDiscover_EmptyServer_ReturnsEmptyResults()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("empty", new MockMcpServerConfig());

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: auto
        type: mcp.call
        input:
          server: empty
    outputs:
      status: "${data.steps.auto.status}"
""", mcpFactory: factory);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        var stepOut = result.StepResults[0].Output as JsonObject;
        var results = stepOut!["results"] as JsonArray;
        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task McpCall_AutoDiscover_InMemoryFactory_EndToEnd()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("demo", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Weather" },
                new() { Name = "search", Description = "Search" }
            },
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "summarize", Description = "Summarize" }
            },
            ToolHandlers = new()
            {
                ["get_weather"] = _ => new McpCallResult { IsError = false, Content = new JsonObject { ["temp"] = 22 } },
                ["search"] = _ => new McpCallResult { IsError = false, Content = new JsonObject { ["hits"] = 5 } }
            },
            PromptHandlers = new()
            {
                ["summarize"] = args => new McpGetPromptResult
                {
                    Description = "Summary",
                    Messages = new List<McpPromptMessage>
                    {
                        new() { Role = "user", Content = "Please summarize" }
                    }
                }
            }
        });

        // Auto-discover tools (kind=tool by default)
        var toolResult = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: auto_tools
        type: mcp.call
        input:
          server: demo
      - id: auto_prompts
        type: mcp.call
        input:
          server: demo
          kind: prompt
    outputs:
      tools_status: "${data.steps.auto_tools.status}"
      prompts_status: "${data.steps.auto_prompts.status}"
""", mcpFactory: factory);

        Assert.True(toolResult.Success);
        Assert.Equal("ok", toolResult.Outputs!["tools_status"]!.GetValue<string>());
        Assert.Equal("ok", toolResult.Outputs["prompts_status"]!.GetValue<string>());

        // Verify tools results
        var toolsOut = toolResult.StepResults[0].Output as JsonObject;
        var toolsResults = toolsOut!["results"] as JsonArray;
        Assert.Equal(2, toolsResults!.Count);
        Assert.Equal("get_weather", toolsResults[0]!["method"]!.GetValue<string>());
        Assert.Equal("search", toolsResults[1]!["method"]!.GetValue<string>());

        // Verify prompts results
        var promptsOut = toolResult.StepResults[1].Output as JsonObject;
        var promptsResults = promptsOut!["results"] as JsonArray;
        Assert.Single(promptsResults!);
        Assert.Equal("summarize", promptsResults[0]!["method"]!.GetValue<string>());
    }
}
