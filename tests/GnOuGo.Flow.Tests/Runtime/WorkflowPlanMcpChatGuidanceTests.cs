using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowPlanMcpChatGuidanceTests
{
    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    [Fact]
    public async Task WorkflowPlan_PromptMentionsLlmAssistedMcpCall()
    {
        string? capturedPrompt = null;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Description = "GitHub repository automation and file operations",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "list_repos", Description = "List repos" }
            }
        });

        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           mode: basic
           generator:
             model: gpt-4
             instruction: Build an MCP workflow
           validate:
             compile: false
 ");

        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        // LLM-assisted guidance still present
        Assert.Contains("use mcp.call with prompt + model (+ optional temperature)", capturedPrompt);
        Assert.Contains("put the natural-language instruction in input.prompt", capturedPrompt);
        // Preferred direct-call pattern when tools are discovered
        Assert.Contains("Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly", capturedPrompt);
        Assert.Contains("preserve JSON schema scalar types exactly", capturedPrompt);
        Assert.Contains("numbers/integers/booleans must be unquoted YAML scalars", capturedPrompt);
        Assert.Contains("prefer a YAML literal block (`|`) so nested quotes remain valid YAML", capturedPrompt);
        // Tool discovered
        Assert.Contains("list_repos", capturedPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_PromptFallsBackWhenDiscoveryFails()
    {
        string? capturedPrompt = null;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        // Use a factory mock that throws on GetClientAsync
        var mockFactory = new Mock<IMcpClientFactory>();
        mockFactory.Setup(f => f.ServerMetadata).Returns(new List<McpServerMetadata>
        {
            new() { Name = "broken-server", Description = "A server that fails to connect" }
        }.AsReadOnly());
        mockFactory.Setup(f => f.GetClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           mode: basic
           generator:
             model: gpt-4
             instruction: test
           validate:
             compile: false
 ");

        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mockFactory.Object
        };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("<available_mcp_servers>", capturedPrompt);
        Assert.Contains("- broken-server: A server that fails to connect", capturedPrompt);
        Assert.Contains("(tool discovery unavailable)", capturedPrompt);
        // Falls back to old discovery pattern when no tools were discovered
        Assert.Contains("Required MCP planning pattern: discover candidate servers", capturedPrompt);
    }

    [Fact]
    public void McpExecutors_DslSnippets_ContainLlmAssistedPattern()
    {
        var engine = new WorkflowEngine();

        var mcpCallSnippet = engine.Registry.Get("mcp.call")?.DslSnippet;
        Assert.NotNull(mcpCallSnippet);
        Assert.Contains("LLM-assisted MCP call pattern:", mcpCallSnippet);
        Assert.Contains("provide a natural-language `prompt` + `model` (+ optional `temperature`)", mcpCallSnippet);
        Assert.Contains("tools: \"${data.steps.discover.tools}\"", mcpCallSnippet);
        Assert.Contains("Output (LLM-assisted):", mcpCallSnippet);
        // New: direct-call guidance when tools are known
        Assert.Contains("Direct MCP call pattern (preferred when tool names are known", mcpCallSnippet);
        Assert.Contains("use `mcp.call` directly with explicit `method` and `request`", mcpCallSnippet);
        // New: output access patterns
        Assert.Contains("Output access patterns:", mcpCallSnippet);
        Assert.Contains("data.steps.<id>.status", mcpCallSnippet);
        Assert.Contains("data.steps.<id>.response", mcpCallSnippet);

        var mcpListSnippet = engine.Registry.Get("mcp.list")?.DslSnippet;
        Assert.NotNull(mcpListSnippet);
        Assert.Contains("can be passed directly into `mcp.call.input.tools` and/or `mcp.call.input.prompts`", mcpListSnippet);
        Assert.Contains("model: gpt-4o-mini", mcpListSnippet);
        Assert.Contains("prompt: \"Choose the right MCP capability and call it\"", mcpListSnippet);
    }
}
