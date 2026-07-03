using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// In-memory mock implementation of IMcpClientFactory for testing and CLI usage.
/// Allows registering mock servers with tools that return predefined responses.
/// </summary>
public sealed class InMemoryMcpClientFactory : IMcpClientFactory
{
    private readonly Dictionary<string, MockMcpServerConfig> _servers = new();

    public IReadOnlyList<McpServerMetadata> ServerMetadata => _servers
        .Select(kv => new McpServerMetadata
        {
            Name = kv.Key,
            Description = kv.Value.Description,
            CallTimeoutSeconds = kv.Value.CallTimeoutSeconds
        })
        .ToList()
        .AsReadOnly();

    /// <summary>
    /// Register a mock MCP server with its available tools.
    /// </summary>
    public void RegisterServer(string name, MockMcpServerConfig config)
    {
        _servers[name] = config;
    }

    public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_servers.TryGetValue(serverName, out var config))
            throw new Expressions.WorkflowRuntimeException(Models.ErrorCodes.McpServerNotFound,
                $"MCP server '{serverName}' not found. Available: [{string.Join(", ", _servers.Keys)}]");

        IMcpSession session = new InMemoryMcpSession(serverName, config);
        return Task.FromResult(session);
    }
}

/// <summary>
/// Configuration for a mock MCP server.
/// </summary>
public sealed class MockMcpServerConfig
{
    /// <summary>Human-friendly description of what this server is for.</summary>
    public string? Description { get; set; }

    /// <summary>Recommended minimum timeout, in seconds, for mcp.call executions.</summary>
    public int? CallTimeoutSeconds { get; set; }

    /// <summary>Available tools on this server.</summary>
    public List<McpToolInfo> Tools { get; set; } = new();

    /// <summary>Available resources on this server.</summary>
    public List<McpResourceInfo> Resources { get; set; } = new();

    /// <summary>Available prompts on this server.</summary>
    public List<McpPromptInfo> Prompts { get; set; } = new();

    /// <summary>
    /// Handler that produces results for tool calls.
    /// Key = tool name. If not found, returns a generic mock response.
    /// </summary>
    public Dictionary<string, Func<JsonNode?, McpCallResult>> ToolHandlers { get; set; } = new();

    /// <summary>
    /// Handler that produces results for prompt calls.
    /// Key = prompt name. If not found, returns a generic mock response.
    /// </summary>
    public Dictionary<string, Func<JsonNode?, McpGetPromptResult>> PromptHandlers { get; set; } = new();
}

/// <summary>
/// In-memory MCP session backed by mock tool handlers.
/// </summary>
internal sealed class InMemoryMcpSession : IMcpSession
{
    private readonly MockMcpServerConfig _config;

    public InMemoryMcpSession(string serverName, MockMcpServerConfig config)
    {
        ServerName = serverName;
        _config = config;
    }

    public string ServerName { get; }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<McpToolInfo> tools = _config.Tools.AsReadOnly();
        return Task.FromResult(McpToolContractEnricher.EnrichTools(tools));
    }

    public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<McpResourceInfo> resources = _config.Resources.AsReadOnly();
        return Task.FromResult(resources);
    }

    public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<McpPromptInfo> prompts = _config.Prompts.AsReadOnly();
        return Task.FromResult(prompts);
    }

    public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_config.ToolHandlers.TryGetValue(toolName, out var handler))
        {
            var result = handler(arguments);
            return Task.FromResult(result);
        }

        // Default mock response
        return Task.FromResult(new McpCallResult
        {
            IsError = false,
            Content = new JsonObject
            {
                ["mock"] = true,
                ["tool"] = toolName,
                ["message"] = $"[Mock MCP] Tool '{toolName}' called on server '{ServerName}'"
            },
            Model = "mock-model",
            Usage = new JsonObject
            {
                ["prompt_tokens"] = 5,
                ["completion_tokens"] = 15,
                ["total_tokens"] = 20
            }
        });
    }

    public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_config.PromptHandlers.TryGetValue(promptName, out var handler))
        {
            var result = handler(arguments);
            return Task.FromResult(result);
        }

        // Default mock response
        return Task.FromResult(new McpGetPromptResult
        {
            Description = $"[Mock MCP] Prompt '{promptName}' on server '{ServerName}'",
            Messages = new List<McpPromptMessage>
            {
                new() { Role = "user", Content = $"[Mock prompt '{promptName}' with args: {arguments?.ToJsonString() ?? "null"}]" }
            },
            Model = "mock-model",
            Usage = new JsonObject
            {
                ["prompt_tokens"] = 8,
                ["completion_tokens"] = 12,
                ["total_tokens"] = 20
            }
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
