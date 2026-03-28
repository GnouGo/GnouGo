using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class McpCallLlmSelectionTests
{
    private static CompiledDocument CompileDoc(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        return new WorkflowCompiler().Compile(doc);
    }

    private static async Task<RunResult> RunMain(string yaml, IMcpClientFactory mcpFactory, ILLMClient llm)
    {
        var compiled = CompileDoc(yaml);
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            McpClientFactory = mcpFactory,
            LLMClient = llm
        };
        return await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
    }

    [Fact]
    public async Task McpCall_LlmAssisted_UsesProvidedToolsAndCallsSelectedTool()
    {
        LLMRequest? captured = null;

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("github");
        mockSession.Setup(s => s.CallToolAsync("repo_stats", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject { ["stars"] = 42 }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new LLMResponse
            {
                Text = "I will call repo_stats.",
                ToolCalls = new List<LLMToolCall>
                {
                    new()
                    {
                        Id = "call_1",
                        Name = "repo_stats",
                        Arguments = new JsonObject { ["owner"] = "me" }
                    }
                }
            });

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: github
          model: gpt-4o-mini
          temperature: 0.2
          prompt: Choose and call the best GitHub tool
          tools:
            - name: repo_stats
              description: Return repository stars
              input_schema:
                type: object
                properties:
                  owner: { type: string }
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("gpt-4o-mini", captured!.Model);
        Assert.Equal(0.2, captured.Temperature);
        Assert.NotNull(captured.Tools);
        Assert.Single(captured.Tools!);
        Assert.Equal("repo_stats", captured.Tools[0].Name);

        mockSession.Verify(s => s.CallToolAsync(
            "repo_stats",
            It.Is<JsonNode?>(n => n != null && n["owner"]!.GetValue<string>() == "me"),
            It.IsAny<CancellationToken>()), Times.Once);

        var stepOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("llm", stepOut["selection_mode"]!.GetValue<string>());
        var results = Assert.IsType<JsonArray>(stepOut["results"]);
        Assert.Single(results);
        Assert.Equal("repo_stats", results[0]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_LlmAssisted_CanUseMcpListOutputsDirectly()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("github");
        mockSession.Setup(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "repo_stats", Description = "Return repository stars", InputSchema = new JsonObject { ["type"] = "object" } }
            }.AsReadOnly());
        mockSession.Setup(s => s.CallToolAsync("repo_stats", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject { ["stars"] = 42 }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = "Calling repo_stats.",
                ToolCalls = new List<LLMToolCall>
                {
                    new()
                    {
                        Name = "repo_stats",
                        Arguments = new JsonObject { ["owner"] = "me" }
                    }
                }
            });

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: discover
        type: mcp.list
        input:
          servers: [github]
          include: [tools]
      - id: call
        type: mcp.call
        input:
          server: github
          model: gpt-4o-mini
          prompt: Choose and call the best GitHub tool
          tools: "${data.steps.discover.tools}"
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);
        mockSession.Verify(s => s.ListToolsAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockSession.Verify(s => s.CallToolAsync("repo_stats", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpCall_LlmAssisted_CanSelectPromptFromProvidedPrompts()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("docs");
        mockSession.Setup(s => s.GetPromptAsync("summarize_document", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpGetPromptResult
            {
                Description = "Summarize document",
                Messages = new List<McpPromptMessage>
                {
                    new() { Role = "user", Content = "Summary prompt resolved" }
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("docs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = "Calling summarize_document.",
                ToolCalls = new List<LLMToolCall>
                {
                    new()
                    {
                        Name = "summarize_document",
                        Arguments = new JsonObject { ["text"] = "Hello" }
                    }
                }
            });

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: docs
          kind: prompt
          model: gpt-4o-mini
          prompt: Choose and call the best document prompt
          prompts:
            - name: summarize_document
              description: Summarize a document
              arguments:
                - name: text
                  description: Text to summarize
                  required: true
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);
        mockSession.Verify(s => s.GetPromptAsync(
            "summarize_document",
            It.Is<JsonNode?>(n => n != null && n["text"]!.GetValue<string>() == "Hello"),
            It.IsAny<CancellationToken>()), Times.Once);

        var stepOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        var results = Assert.IsType<JsonArray>(stepOut["results"]);
        Assert.Equal("prompt", results[0]!["kind"]!.GetValue<string>());
        Assert.Equal("summarize_document", results[0]!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_LlmAssisted_PromptFallback_UnsupportedPromptsList_ReturnsNoCapabilitiesWithoutCallingLlm()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("docs");
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Method 'prompts/list' is not available."));
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("docs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>(MockBehavior.Strict);

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: docs
          kind: prompt
          model: gpt-4o-mini
          prompt: Choose and call the best document prompt
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);

        var stepOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("ok", stepOut["status"]!.GetValue<string>());
        Assert.Equal("llm", stepOut["selection_mode"]!.GetValue<string>());
        Assert.Equal("No MCP capabilities available for selection.", stepOut["text"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(stepOut["tool_calls"]));
        Assert.Empty(Assert.IsType<JsonArray>(stepOut["results"]));

        mockLlm.Verify(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        mockSession.Verify(s => s.GetPromptAsync(It.IsAny<string>(), It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpCall_LlmAssisted_PromptFallback_RealPromptsListError_Fails()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("docs");
        mockSession.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection failed"));
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("docs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>(MockBehavior.Strict);

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: call
        type: mcp.call
        input:
          server: docs
          kind: prompt
          model: gpt-4o-mini
          prompt: Choose and call the best document prompt
""", mockFactory.Object, mockLlm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.McpPromptError, result.Error!.Code);
        Assert.Contains("connection failed", result.Error.Message);
        mockLlm.Verify(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpCall_LlmAssisted_StructuredOutput_RunsFinalFormattingPass()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("browser");
        mockSession.Setup(s => s.CallToolAsync("browser_get_content", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject
                {
                    ["content"] = "<nav><a href='https://slimfaas.dev/docs'>Docs</a></nav>"
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("browser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(() =>
            {
                if (requests.Count == 1)
                {
                    return new LLMResponse
                    {
                        Text = "I will inspect rendered HTML.",
                        ToolCalls = new List<LLMToolCall>
                        {
                            new()
                            {
                                Name = "browser_get_content",
                                Arguments = new JsonObject
                                {
                                    ["url"] = "https://slimfaas.dev",
                                    ["selector"] = "nav",
                                    ["format"] = "html"
                                }
                            }
                        },
                        Usage = new JsonObject
                        {
                            ["prompt_tokens"] = 10,
                            ["completion_tokens"] = 5,
                            ["total_tokens"] = 15
                        }
                    };
                }

                return new LLMResponse
                {
                    Text = "{\"links\":[{\"title\":\"Docs\",\"url\":\"https://slimfaas.dev/docs\"}]}",
                    Json = new JsonObject
                    {
                        ["links"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["title"] = "Docs",
                                ["url"] = "https://slimfaas.dev/docs"
                            }
                        }
                    },
                    Usage = new JsonObject
                    {
                        ["prompt_tokens"] = 20,
                        ["completion_tokens"] = 8,
                        ["total_tokens"] = 28
                    }
                };
            });

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: scrape
        type: mcp.call
        input:
          server: browser
          model: gpt-4o-mini
          prompt: >
            Va sur https://slimfaas.dev en mode HTML, identifie le menu principal de navigation,
            puis extrais tous les liens du menu.
            Retourne un JSON strict avec la forme {"links":[{"title":"...","url":"..."}]}.
            Utilise des URLs absolues.
          tools:
            - name: browser_get_content
              description: Read rendered page content.
              input_schema:
                type: object
                properties:
                  selector: { type: string }
                  url: { type: string }
                  format:
                    type: string
                    enum: [text, html]
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
            strict: true
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal(2, requests.Count);
        Assert.NotNull(requests[0].Tools);
        Assert.Null(requests[0].StructuredOutputSchema);
        Assert.Null(requests[1].Tools);
        Assert.NotNull(requests[1].StructuredOutputSchema);
        Assert.Contains("browser_get_content", requests[1].Prompt);

        mockSession.Verify(s => s.CallToolAsync(
            "browser_get_content",
            It.Is<JsonNode?>(n => n != null
                && n["url"]!.GetValue<string>() == "https://slimfaas.dev"
                && n["format"]!.GetValue<string>() == "html"),
            It.IsAny<CancellationToken>()), Times.Once);

        var stepOut = Assert.IsType<JsonObject>(result.StepResults[0].Output);
        Assert.Equal("llm", stepOut["selection_mode"]!.GetValue<string>());
        var json = Assert.IsType<JsonObject>(stepOut["json"]);
        var links = Assert.IsType<JsonArray>(json["links"]);
        Assert.Single(links);
        Assert.Equal("Docs", links[0]!["title"]!.GetValue<string>());
        Assert.Equal("https://slimfaas.dev/docs", links[0]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpCall_LlmAssisted_SelectionPrompt_ContainsHtmlGuidance()
    {
        LLMRequest? captured = null;

        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("browser");
        mockSession.Setup(s => s.CallToolAsync("browser_get_content", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult { IsError = false, Content = new JsonObject() });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("browser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new LLMResponse
            {
                ToolCalls = new List<LLMToolCall>
                {
                    new() { Name = "browser_get_content", Arguments = new JsonObject { ["format"] = "html" } }
                }
            });

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: scrape
        type: mcp.call
        input:
          server: browser
          model: gpt-4o-mini
          prompt: Read the rendered HTML and extract the main navigation links.
          tools:
            - name: browser_get_content
              description: Read rendered page content.
              input_schema:
                type: object
                properties:
                  format: { type: string }
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Contains("Preserve every explicit user constraint", captured!.Prompt);
        Assert.Contains("Choose the smallest set of MCP calls", captured.Prompt);
        Assert.Contains("Read the rendered HTML and extract the main navigation links", captured.Prompt);
    }

    [Fact]
    public async Task McpCall_LlmAssisted_ForwardsUrlToOneShotBrowserContentTool()
    {
        var mockSession = new Mock<IMcpSession>();
        mockSession.Setup(s => s.ServerName).Returns("browser");
        mockSession.Setup(s => s.CallToolAsync("browser_get_content", It.IsAny<JsonNode?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpCallResult
            {
                IsError = false,
                Content = new JsonObject
                {
                    ["url"] = "https://example.com",
                    ["format"] = "html",
                    ["content"] = "<html></html>"
                }
            });
        mockSession.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.GetClientAsync("browser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockSession.Object);

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                ToolCalls = new List<LLMToolCall>
                {
                    new()
                    {
                        Name = "browser_get_content",
                        Arguments = new JsonObject
                        {
                            ["url"] = "https://example.com",
                            ["waitUntil"] = "domcontentloaded",
                            ["format"] = "html",
                            ["maxCharacters"] = 5000
                        }
                    }
                }
            });

        var result = await RunMain("""
dsl: 1
workflows:
  main:
    steps:
      - id: scrape
        type: mcp.call
        input:
          server: browser
          model: gpt-4o-mini
          prompt: Open https://example.com and return the rendered HTML.
          tools:
            - name: browser_get_content
              description: Open a page and read rendered content.
              input_schema:
                type: object
                properties:
                  url: { type: string }
                  waitUntil: { type: string }
                  format: { type: string }
                  maxCharacters: { type: number }
""", mockFactory.Object, mockLlm.Object);

        Assert.True(result.Success);
        mockSession.Verify(s => s.CallToolAsync(
            "browser_get_content",
            It.Is<JsonNode?>(n => n != null
                && n["url"]!.GetValue<string>() == "https://example.com"
                && n["waitUntil"]!.GetValue<string>() == "domcontentloaded"
                && n["format"]!.GetValue<string>() == "html"
                && n["maxCharacters"]!.GetValue<int>() == 5000),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

