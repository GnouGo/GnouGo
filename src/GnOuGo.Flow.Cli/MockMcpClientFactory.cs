using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Cli;

/// <summary>
/// Mock MCP client factory that returns deterministic tool responses for testing.
/// </summary>
internal sealed class MockMcpClientFactory : IMcpClientFactory
{
    private readonly IReadOnlyList<McpServerMetadata> _serverMetadata =
        new List<McpServerMetadata>
        {
            new() { Name = "mock", Description = "Mock MCP server for local CLI testing" }
        };

    public IReadOnlyList<McpServerMetadata> ServerMetadata => _serverMetadata;

    public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
    {
        return Task.FromResult<IMcpSession>(new MockMcpSession(serverName));
    }
}

internal sealed class MockMcpSession : IMcpSession
{
    public string ServerName { get; }

    public MockMcpSession(string serverName)
    {
        ServerName = serverName;
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>
        {
            new McpToolInfo
            {
                Name = "mock_tool",
                Description = $"Mock tool on server '{ServerName}'",
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string" }
                    }
                }
            }
        };
        return Task.FromResult<IReadOnlyList<McpToolInfo>>(tools);
    }

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<McpResourceInfo>>(new List<McpResourceInfo>());
    }

    public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<McpPromptInfo>>(new List<McpPromptInfo>());
    }

    public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
    {
        var result = new McpCallResult
        {
            IsError = false,
            Content = new JsonObject
            {
                ["mock"] = true,
                ["message"] = $"[Mock MCP] Called {toolName} on {ServerName}",
                ["args"] = arguments?.DeepClone()
            }
        };
        return Task.FromResult(result);
    }

    public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
    {
        var result = new McpGetPromptResult
        {
            Description = $"Mock prompt '{promptName}'",
            Messages = new List<McpPromptMessage>
            {
                new McpPromptMessage { Role = "user", Content = $"Mock prompt content for {promptName}" }
            }
        };
        return Task.FromResult(result);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
