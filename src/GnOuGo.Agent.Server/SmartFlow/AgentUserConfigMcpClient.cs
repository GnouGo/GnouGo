using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

public sealed record AgentUserConfigSnapshot(
    string? DefaultLlmProvider,
    string? DefaultLlmModel,
    string? DefaultEmbeddingConfig,
    string? DefaultAgent,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// Reads and writes persisted user defaults through the locally mounted Agent MCP endpoint.
/// </summary>
public sealed class AgentUserConfigMcpClient
{
    private readonly LLMRuntimeOptionsStore _optionsStore;
    private readonly ILogger<AgentUserConfigMcpClient> _logger;

    public AgentUserConfigMcpClient(
        LLMRuntimeOptionsStore optionsStore,
        ILogger<AgentUserConfigMcpClient> logger)
    {
        _optionsStore = optionsStore;
        _logger = logger;
    }

    public async Task<AgentUserConfigSnapshot> GetAsync(CancellationToken ct = default)
    {
        var response = await CallToolAsync("user_config_get", arguments: null, ct);
        return response is null ? Empty() : ParseSnapshot(response);
    }

    public async Task<AgentUserConfigSnapshot?> SetAsync(
        string? defaultLlmProvider = null,
        string? defaultLlmModel = null,
        string? defaultEmbeddingConfig = null,
        string? defaultAgent = null,
        bool clearDefaultLlm = false,
        bool clearDefaultEmbedding = false,
        bool clearDefaultAgent = false,
        CancellationToken ct = default)
    {
        var arguments = new JsonObject();
        if (defaultLlmProvider is not null)
            arguments["defaultLlmProvider"] = defaultLlmProvider;
        if (defaultLlmModel is not null)
            arguments["defaultLlmModel"] = defaultLlmModel;
        if (defaultEmbeddingConfig is not null)
            arguments["defaultEmbeddingConfig"] = defaultEmbeddingConfig;
        if (defaultAgent is not null)
            arguments["defaultAgent"] = defaultAgent;
        if (clearDefaultLlm)
            arguments["clearDefaultLlm"] = true;
        if (clearDefaultEmbedding)
            arguments["clearDefaultEmbedding"] = true;
        if (clearDefaultAgent)
            arguments["clearDefaultAgent"] = true;

        var response = await CallToolAsync("user_config_set", arguments, ct);
        return response is null ? null : ParseSnapshot(response);
    }

    private async Task<JsonObject?> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
    {
        if (!TryGetAgentMcpOptions(out var agentMcpOptions))
            return null;

        try
        {
            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [AgentMcpHostingExtensions.ServerName] = CloneServerOptions(agentMcpOptions)
            });

            await using var session = await factory.GetClientAsync(AgentMcpHostingExtensions.ServerName, ct);
            var result = await session.CallToolAsync(toolName, arguments, ct);
            if (result.IsError)
            {
                _logger.LogWarning("Agent MCP tool '{ToolName}' returned an MCP transport-level error.", toolName);
                return null;
            }

            var payload = result.Content as JsonObject;
            if (payload is null)
            {
                _logger.LogWarning("Agent MCP tool '{ToolName}' returned an unexpected payload shape.", toolName);
                return null;
            }

            var success = payload["success"]?.GetValue<bool>() ?? false;
            if (!success)
            {
                _logger.LogWarning(
                    "Agent MCP tool '{ToolName}' failed: {ErrorCode} {ErrorMessage}",
                    toolName,
                    payload["error_code"]?.GetValue<string>(),
                    payload["error_message"]?.GetValue<string>());
                return null;
            }

            return payload;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not call Agent MCP tool '{ToolName}'.", toolName);
            return null;
        }
    }

    private bool TryGetAgentMcpOptions(out McpServerOptions options)
    {
        if (_optionsStore.Current.McpServers.TryGetValue(AgentMcpHostingExtensions.ServerName, out var configured)
            && !string.IsNullOrWhiteSpace(configured.Url)
            && !configured.Url.Contains(":0/", StringComparison.Ordinal))
        {
            options = configured;
            return true;
        }

        _logger.LogDebug("Agent MCP user config client skipped because the mounted MCP endpoint is not ready yet.");
        options = new McpServerOptions();
        return false;
    }

    private static AgentUserConfigSnapshot ParseSnapshot(JsonObject payload)
    {
        var config = payload["config"] as JsonObject;
        if (config is null)
            return Empty();

        return new AgentUserConfigSnapshot(
            DefaultLlmProvider: config["default_llm_provider"]?.GetValue<string>(),
            DefaultLlmModel: config["default_llm_model"]?.GetValue<string>(),
            DefaultEmbeddingConfig: config["default_embedding_config"]?.GetValue<string>(),
            DefaultAgent: config["default_agent"]?.GetValue<string>(),
            UpdatedAt: DateTimeOffset.TryParse(config["updated_at"]?.GetValue<string>(), out var updatedAt) ? updatedAt : null);
    }

    private static AgentUserConfigSnapshot Empty()
        => new(null, null, null, null, null);

    private static McpServerOptions CloneServerOptions(McpServerOptions source)
        => new()
        {
            Type = source.Type,
            Description = source.Description,
            Url = source.Url,
            ApiKey = source.ApiKey,
            Issuer = source.Issuer,
            ClientId = source.ClientId,
            ClientSecret = source.ClientSecret,
            Scopes = source.Scopes,
            Command = source.Command,
            Args = source.Args is null ? null : [.. source.Args]
        };
}

