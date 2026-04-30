using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class McpListExecutorTests
{
    private static CompiledDocument CompileDoc(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        return new WorkflowCompiler().Compile(doc);
    }

    private static async Task<RunResult> RunMain(string yaml, JsonObject? inputs = null,
        IMcpClientFactory? mcpFactory = null)
    {
        var compiled = CompileDoc(yaml);
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            McpClientFactory = mcpFactory,
        };
        return await engine.ExecuteAsync(wf, inputs ?? new JsonObject(), CancellationToken.None);
    }

    // ────── Basic mcp.list — tools only (default) ──────

    [Fact]
    public async Task McpList_DefaultInclude_ListsToolsOnly()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping tool" },
                new() { Name = "echo", Description = "Echo tool" }
            }.AsReadOnly());
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
    outputs:
      status: "${data.steps.list.status}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        var listOut = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(listOut);
        var servers = Assert.IsType<JsonArray>(listOut["servers"]);
        Assert.Single(servers);
        Assert.Equal("srv", servers[0]!["name"]!.GetValue<string>());
        Assert.NotNull(listOut["tools"]);
        Assert.Null(listOut["resources"]);
        Assert.Null(listOut["prompts"]);

        var tools = listOut["tools"] as JsonArray;
        Assert.Equal(2, tools!.Count);
        Assert.Equal("ping", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("srv", tools[0]!["server"]!.GetValue<string>());

        var text = listOut["text"]!.GetValue<string>();
        Assert.Contains("ping", text);
        Assert.Contains("echo", text);
    }

    // ────── mcp.list — all three ──────

    [Fact]
    public async Task McpList_AllIncludes_ListsToolsResourcesPrompts()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "search", Description = "Search" }
            }.AsReadOnly());
        mockSession.Setup(s => s.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpResourceInfo>
            {
                new() { Uri = "file:///data.json", Name = "data", Description = "Data file", MimeType = "application/json" }
            }.AsReadOnly());
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpPromptInfo>
            {
                new()
                {
                    Name = "summarize",
                    Description = "Summarize text",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "text", Description = "The text", Required = true }
                    }
                }
            }.AsReadOnly());
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - tools
            - resources
            - prompts
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        var listOut = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(listOut);
        Assert.Equal("ok", listOut["status"]!.GetValue<string>());
        var servers = Assert.IsType<JsonArray>(listOut["servers"]);
        Assert.Single(servers);
        Assert.Equal("srv", servers[0]!["name"]!.GetValue<string>());
        Assert.Equal("ok", servers[0]!["status"]!.GetValue<string>());

        // Tools
        var tools = listOut["tools"] as JsonArray;
        Assert.NotNull(tools);
        Assert.Single(tools);
        Assert.Equal("search", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("srv", tools[0]!["server"]!.GetValue<string>());

        // Resources
        var resources = listOut["resources"] as JsonArray;
        Assert.NotNull(resources);
        Assert.Single(resources);
        Assert.Equal("data", resources[0]!["name"]!.GetValue<string>());
        Assert.Equal("file:///data.json", resources[0]!["uri"]!.GetValue<string>());
        Assert.Equal("application/json", resources[0]!["mime_type"]!.GetValue<string>());
        Assert.Equal("srv", resources[0]!["server"]!.GetValue<string>());

        // Prompts
        var prompts = listOut["prompts"] as JsonArray;
        Assert.NotNull(prompts);
        Assert.Single(prompts);
        Assert.Equal("summarize", prompts[0]!["name"]!.GetValue<string>());
        Assert.Equal("srv", prompts[0]!["server"]!.GetValue<string>());
        var args = prompts[0]!["arguments"] as JsonArray;
        Assert.NotNull(args);
        Assert.Single(args);
        Assert.Equal("text", args[0]!["name"]!.GetValue<string>());

        // Merged text
        var text = listOut["text"]!.GetValue<string>();
        Assert.Contains("Tools", text);
        Assert.Contains("Resources", text);
        Assert.Contains("Prompts", text);
        Assert.Contains("search", text);
        Assert.Contains("data", text);
        Assert.Contains("summarize", text);
    }

    // ────── mcp.list — resources only ──────

    [Fact]
    public async Task McpList_ResourcesOnly_ListsResourcesOnly()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpResourceInfo>
            {
                new() { Uri = "file:///a.txt", Name = "a", Description = "File A" }
            }.AsReadOnly());
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - resources
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        var listOut = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(listOut);
        Assert.Null(listOut["tools"]);
        Assert.NotNull(listOut["resources"]);
        Assert.Null(listOut["prompts"]);
    }

    // ────── mcp.list — prompts only ──────

    [Fact]
    public async Task McpList_PromptsOnly_ListsPromptsOnly()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpPromptInfo>
            {
                new() { Name = "greet", Description = "Greeting prompt" }
            }.AsReadOnly());
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - prompts
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        var listOut = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(listOut);
        Assert.Null(listOut["tools"]);
        Assert.Null(listOut["resources"]);
        var prompts = Assert.IsType<JsonArray>(listOut["prompts"]);
        Assert.Single(prompts);

        var text = listOut["text"]!.GetValue<string>();
        Assert.Contains("Prompts", text);
        Assert.Contains("greet", text);
        Assert.DoesNotContain("Tools", text);
    }

    [Fact]
    public async Task McpList_ToolsAndUnsupportedPrompts_ReturnsEmptyPromptsAndSucceeds()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "navigate", Description = "Navigate in browser" }
            }.AsReadOnly());
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
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - tools
            - prompts
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        var listOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("ok", listOut["status"]!.GetValue<string>());

        var tools = Assert.IsType<JsonArray>(listOut["tools"]);
        Assert.Single(tools);
        Assert.Equal("navigate", tools[0]!["name"]!.GetValue<string>());

        var prompts = Assert.IsType<JsonArray>(listOut["prompts"]);
        Assert.Empty(prompts);

        var text = listOut["text"]!.GetValue<string>();
        Assert.Contains("Tools (1)", text);
        Assert.Contains("Prompts (0)", text);
    }

    [Fact]
    public async Task McpList_UnsupportedResourcesOnly_ReturnsEmptyResourcesAndSucceeds()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Method 'resources/list' is not available."));
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - resources
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        var listOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("ok", listOut["status"]!.GetValue<string>());

        var resources = Assert.IsType<JsonArray>(listOut["resources"]);
        Assert.Empty(resources);

        var text = listOut["text"]!.GetValue<string>();
        Assert.Contains("Resources (0)", text);
    }

    // ────── mcp.list — empty results ──────

    [Fact]
    public async Task McpList_EmptyResults_ReturnsEmptyArrays()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>().AsReadOnly());
        mockSession.Setup(s => s.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpResourceInfo>().AsReadOnly());
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - tools
            - resources
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        var listOut = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(listOut);
        Assert.Empty(Assert.IsType<JsonArray>(listOut["tools"]));
        Assert.Empty(Assert.IsType<JsonArray>(listOut["resources"]));
    }

    // ────── Error handling ──────

    [Fact]
    public async Task McpList_NoFactory_Fails()
    {
        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [any]
""", mcpFactory: null);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpConnectionError, result.Error!.Code);
    }

    [Fact]
    public async Task McpList_MissingServers_Fails()
    {
        var mockFactory = new Mock<IMcpClientFactory>();

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          include:
            - tools
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
    }

    [Fact]
    public async Task McpList_InvalidIncludeValue_Fails()
    {
        var mockFactory = new Mock<IMcpClientFactory>();
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
          include:
            - invalid_type
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
    }

    [Fact]
    public async Task McpList_UnknownServer_Fails()
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
      - id: list
        type: mcp.list
        input:
          servers: [unknown]
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpServerNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task McpList_GenericException_WrapsAsMcpListError()
    {
        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("srv", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection failed"));

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [srv]
""", mcpFactory: mockFactory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpListError, result.Error!.Code);
        Assert.Contains("connection failed", result.Error.Message);
    }

    [Fact]
    public async Task McpList_MultipleServers_FlattensCapabilitiesAndPreservesServerNames()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("alpha", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping alpha" }
            },
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "summarize", Description = "Summarize alpha" }
            }
        });
        factory.RegisterServer("beta", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "search", Description = "Search beta" }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [alpha, beta]
          include:
            - tools
            - prompts
""", mcpFactory: factory);

        Assert.True(result.Success);

        var listOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("ok", listOut["status"]!.GetValue<string>());

        var servers = Assert.IsType<JsonArray>(listOut["servers"]);
        Assert.Equal(2, servers.Count);
        Assert.Equal("alpha", servers[0]!["name"]!.GetValue<string>());
        Assert.Equal("beta", servers[1]!["name"]!.GetValue<string>());

        var tools = Assert.IsType<JsonArray>(listOut["tools"]);
        Assert.Equal(2, tools.Count);
        Assert.Equal("alpha", tools[0]!["server"]!.GetValue<string>());
        Assert.Equal("beta", tools[1]!["server"]!.GetValue<string>());

        var prompts = Assert.IsType<JsonArray>(listOut["prompts"]);
        Assert.Single(prompts);
        Assert.Equal("alpha", prompts[0]!["server"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpList_WildcardServers_ListsAllConfiguredServers()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("alpha", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping alpha" }
            }
        });
        factory.RegisterServer("beta", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "search", Description = "Search beta" }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: ["*"]
""", mcpFactory: factory);

        Assert.True(result.Success);

        var listOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        var servers = Assert.IsType<JsonArray>(listOut["servers"]);
        Assert.Equal(2, servers.Count);

        var tools = Assert.IsType<JsonArray>(listOut["tools"]);
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t!["server"]!.GetValue<string>() == "alpha");
        Assert.Contains(tools, t => t!["server"]!.GetValue<string>() == "beta");
    }

    [Fact]
    public async Task McpList_MultipleServers_PartialFailure_ReturnsPartial()
    {
        var goodSession = new Mock<IMcpSession>();
        goodSession.Setup(s => s.ServerName).Returns("good");
        goodSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping good" }
            }.AsReadOnly());
        goodSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var factory = new Mock<IMcpClientFactory>();
        factory.SetupGet(f => f.ServerMetadata).Returns(new List<McpServerMetadata>
        {
            new() { Name = "good" },
            new() { Name = "bad" }
        }.AsReadOnly());
        factory.Setup(f => f.GetClientAsync("good", It.IsAny<CancellationToken>()))
            .ReturnsAsync(goodSession.Object);
        factory.Setup(f => f.GetClientAsync("bad", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broken connection"));

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [good, bad]
""", mcpFactory: factory.Object);

        Assert.True(result.Success);

        var listOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("partial", listOut["status"]!.GetValue<string>());

        var servers = Assert.IsType<JsonArray>(listOut["servers"]);
        Assert.Equal(2, servers.Count);
        Assert.Equal("ok", servers[0]!["status"]!.GetValue<string>());
        Assert.Equal("error", servers[1]!["status"]!.GetValue<string>());
        Assert.Contains("broken connection", servers[1]!["error"]!.GetValue<string>());

        var tools = Assert.IsType<JsonArray>(listOut["tools"]);
        Assert.Single(tools);
        Assert.Equal("good", tools[0]!["server"]!.GetValue<string>());
    }

    // ────── InMemoryMcpClientFactory ──────

    [Fact]
    public async Task InMemoryFactory_ListResources_ReturnsConfigured()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("test", new MockMcpServerConfig
        {
            Resources = new List<McpResourceInfo>
            {
                new() { Uri = "file:///a.txt", Name = "a", Description = "File A" }
            }
        });

        await using var session = await factory.GetClientAsync("test", CancellationToken.None);
        var resources = await session.ListResourcesAsync(CancellationToken.None);
        Assert.Single(resources);
        Assert.Equal("a", resources[0].Name);
    }

    [Fact]
    public async Task InMemoryFactory_ListPrompts_ReturnsConfigured()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("test", new MockMcpServerConfig
        {
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "greet", Description = "Greeting" }
            }
        });

        await using var session = await factory.GetClientAsync("test", CancellationToken.None);
        var prompts = await session.ListPromptsAsync(CancellationToken.None);
        Assert.Single(prompts);
        Assert.Equal("greet", prompts[0].Name);
    }

    // ────── Validator ──────

    [Fact]
    public void Validate_McpListStepType_IsKnown()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [test]
""";
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var errors = compiler.Validate(doc);
        Assert.DoesNotContain(errors, e => e.Code == ErrorCodes.StepTypeUnknown);
    }

    // ────── Integration with InMemoryFactory ──────

    [Fact]
    public async Task McpList_Integration_WithInMemoryFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("demo", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "ping", Description = "Ping" },
                new() { Name = "echo", Description = "Echo" }
            },
            Resources = new List<McpResourceInfo>
            {
                new() { Uri = "file:///config.json", Name = "config", Description = "Config", MimeType = "application/json" }
            },
            Prompts = new List<McpPromptInfo>
            {
                new()
                {
                    Name = "summarize",
                    Description = "Summarize",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "text", Required = true }
                    }
                }
            }
        });

        var result = await RunMain("""
version: 1
workflows:
  main:
    steps:
      - id: list
        type: mcp.list
        input:
          servers: [demo]
          include:
            - tools
            - resources
            - prompts
    outputs:
      status: "${data.steps.list.status}"
      text: "${data.steps.list.text}"
""", mcpFactory: factory);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Outputs!["status"]!.GetValue<string>());

        var text = result.Outputs["text"]!.GetValue<string>();
        Assert.Contains("ping", text);
        Assert.Contains("echo", text);
        Assert.Contains("config", text);
        Assert.Contains("summarize", text);
    }

    // ────── List-then-Call pattern: mcp.list → loop.parallel → mcp.call ──────

    [Fact]
    public async Task McpList_ThenCall_LoopOverDiscoveredTools()
    {
        // Track how many times ListToolsAsync is called vs CallToolAsync
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
                    new() { Name = "ping", Description = "Ping tool" },
                    new() { Name = "echo", Description = "Echo tool" },
                    new() { Name = "search", Description = "Search tool" }
                }.AsReadOnly();
            });
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpPromptInfo>
            {
                new() { Name = "summarize", Description = "Summarize text" }
            }.AsReadOnly());
        mockSession.Setup(s => s.CallToolAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string toolName, JsonNode? args, CancellationToken _) =>
            {
                Interlocked.Increment(ref callToolCalls);
                return new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["tool"] = toolName, ["result"] = $"ok from {toolName}" }
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
      # Step 1: Discover tools and prompts (single server call)
      - id: discover
        type: mcp.list
        input:
          servers: [srv]
          include:
            - tools
            - prompts

      # Step 2: Loop over discovered tools, call each via mcp.call
      - id: call_all
        type: loop.parallel
        input:
          items: "${data.steps.discover.tools}"
          max_concurrency: 3
        item_var: tool
        steps:
          - id: invoke
            type: mcp.call
            input:
              server: srv
              method: "${data.tool.name}"
              request:
                source: "auto-discovered"

    outputs:
      tool_count: "${len(data.steps.discover.tools)}"
      prompt_count: "${len(data.steps.discover.prompts)}"
      calls_made: "${data.steps.call_all.count}"
      discover_text: "${data.steps.discover.text}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);

        // Verify: mcp.list called ListToolsAsync exactly once
        Assert.Equal(1, listToolsCalls);

        // Verify: mcp.call called CallToolAsync 3 times (one per discovered tool)
        Assert.Equal(3, callToolCalls);

        // Verify outputs
        Assert.Equal(3, (int)ExpressionEvaluator.GetNumber(result.Outputs!["tool_count"]));
        Assert.Equal(1, (int)ExpressionEvaluator.GetNumber(result.Outputs["prompt_count"]));
        Assert.Equal(3, (int)ExpressionEvaluator.GetNumber(result.Outputs["calls_made"]));

        // Verify text contains all discovered items
        var discoverText = result.Outputs["discover_text"]!.GetValue<string>();
        Assert.Contains("ping", discoverText);
        Assert.Contains("echo", discoverText);
        Assert.Contains("search", discoverText);
        Assert.Contains("summarize", discoverText);
    }

    [Fact]
    public async Task McpList_ThenCall_SequentialLoopOverTools()
    {
        var calledTools = new List<string>();

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("srv");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "alpha", Description = "Alpha tool" },
                new() { Name = "beta", Description = "Beta tool" }
            }.AsReadOnly());
        mockSession.Setup(s => s.CallToolAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string toolName, JsonNode? args, CancellationToken _) =>
            {
                lock (calledTools) calledTools.Add(toolName);
                return new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["ok"] = true }
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

      - id: call_each
        type: loop.parallel
        input:
          items: "${data.steps.discover.tools}"
        item_var: t
        steps:
          - id: do_call
            type: mcp.call
            input:
              server: srv
              method: "${data.t.name}"

    outputs:
      called: "${data.steps.call_each.count}"
""", mcpFactory: mockFactory.Object);

        Assert.True(result.Success);
        Assert.Equal(2, (int)ExpressionEvaluator.GetNumber(result.Outputs!["called"]));
        Assert.Contains("alpha", calledTools);
        Assert.Contains("beta", calledTools);
    }

    [Fact]
    public async Task McpList_ThenCall_WithInMemoryFactory_EndToEnd()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("demo", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Get weather" },
                new() { Name = "search", Description = "Search the web" }
            },
            Prompts = new List<McpPromptInfo>
            {
                new()
                {
                    Name = "summarize",
                    Description = "Summarize",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "text", Required = true }
                    }
                }
            },
            ToolHandlers = new()
            {
                ["get_weather"] = args => new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["temp"] = 22, ["city"] = "Paris" }
                },
                ["search"] = args => new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["results"] = new JsonArray(JsonValue.Create("result1")) }
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
        item_var: tool
        steps:
          - id: invoke
            type: mcp.call
            input:
              server: demo
              method: "${data.tool.name}"

    outputs:
      tool_count: "${len(data.steps.discover.tools)}"
      prompt_count: "${len(data.steps.discover.prompts)}"
      calls_made: "${data.steps.call_tools.count}"
      discover_text: "${data.steps.discover.text}"
""", mcpFactory: factory);

        Assert.True(result.Success);

        // 2 tools discovered
        Assert.Equal(2, (int)ExpressionEvaluator.GetNumber(result.Outputs!["tool_count"]));
        // 1 prompt discovered
        Assert.Equal(1, (int)ExpressionEvaluator.GetNumber(result.Outputs["prompt_count"]));
        // 2 tools called
        Assert.Equal(2, (int)ExpressionEvaluator.GetNumber(result.Outputs["calls_made"]));

        // Text includes all names
        var text = result.Outputs["discover_text"]!.GetValue<string>();
        Assert.Contains("get_weather", text);
        Assert.Contains("search", text);
        Assert.Contains("summarize", text);
    }
}



