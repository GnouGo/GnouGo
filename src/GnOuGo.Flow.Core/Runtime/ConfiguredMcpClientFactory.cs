using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Real <see cref="IMcpClientFactory"/> implementation that connects to MCP servers
/// using the Microsoft ModelContextProtocol library (&gt;= 1.0.0).
/// Reads configuration from a dictionary of <see cref="McpServerOptions"/>.
/// Shared by both GnOuGo.Flow.Cli and GnOuGo.Flow.Server.
/// </summary>
public sealed class ConfiguredMcpClientFactory : IMcpClientFactory, IAsyncDisposable
{
    private readonly Dictionary<string, McpServerOptions> _serverConfigs;
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly IReadOnlyList<McpServerMetadata> _serverMetadata;

    public ConfiguredMcpClientFactory(Dictionary<string, McpServerOptions> serverConfigs)
    {
        _serverConfigs = serverConfigs;
        _serverMetadata = _serverConfigs
            .Select(kv => new McpServerMetadata
            {
                Name = kv.Key,
                Description = kv.Value.Description
            })
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<McpServerMetadata> ServerMetadata => _serverMetadata;

    public async Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
    {
        if (!_serverConfigs.TryGetValue(serverName, out var config))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.McpServerNotFound,
                $"MCP server '{serverName}' not found. Available: [{string.Join(", ", _serverConfigs.Keys)}]");
        }

        if (!_clients.TryGetValue(serverName, out var client))
        {
            client = await CreateClientAsync(serverName, config, ct);
            _clients.TryAdd(serverName, client);
        }

        return new McpSessionAdapter(serverName, client);
    }

    private static async Task<McpClient> CreateClientAsync(
        string serverName, McpServerOptions config, CancellationToken ct)
    {
        var type = config.Type?.ToLowerInvariant() ?? "http";

        IClientTransport transport = type switch
        {
            "http" or "sse" => CreateHttpTransport(config),
            "stdio" => CreateStdioTransport(config),
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.McpConnectionError,
                $"Unknown MCP transport type '{config.Type}' for server '{serverName}'")
        };

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "GnOuGo.Flow", Version = "1.0.0" }
        }, cancellationToken: ct);
    }

    private static HttpClientTransport CreateHttpTransport(McpServerOptions config)
    {
        var endpoint = new Uri(config.Url.TrimEnd('/'));
        var preferHttp2 = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || IsLoopbackHttpEndpoint(endpoint);
        var httpClient = new HttpClient(new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
            // Mounted/local MCP endpoints may require HTTP/2 even over loopback HTTP
            // (h2/h2c). Keep those endpoints on HTTP/2 negotiation so the MCP client
            // does not downgrade to the legacy SSE/session-header flow.
            DefaultRequestVersion = preferHttp2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = preferHttp2
                ? HttpVersionPolicy.RequestVersionOrHigher
                : HttpVersionPolicy.RequestVersionOrLower
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "GnOuGo.Flow"
        }, httpClient);
    }

    private static bool IsLoopbackHttpEndpoint(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        if (endpoint.IsLoopback)
            return true;

        return IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static StdioClientTransport CreateStdioTransport(McpServerOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            throw new WorkflowRuntimeException(
                ErrorCodes.McpConnectionError,
                "MCP stdio transport requires a 'Command'");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = config.Command,
            Arguments = config.Args ?? [],
            Name = "GnOuGo.Flow"
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();
    }

    /// <summary>
    /// Returns <c>true</c> when the exception indicates an MCP server that has
    /// disconnected or exited unexpectedly, so the caller can decide to reconnect
    /// rather than propagate the error.
    /// </summary>
    internal static bool IsUnexpectedServerExit(Exception ex)
    {
        // Walk the exception chain so we catch nested causes too.
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.Message.Contains("MCP server process exited unexpectedly", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Known transport-level disconnection messages.
        var msg = ex.Message;
        if (msg.Contains("The pipe is broken", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("The connection is closed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("Cannot access a disposed object", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

/// <summary>
/// Adapts a <see cref="McpClient"/> from the Microsoft library
/// to the <see cref="IMcpSession"/> interface used by GnOuGo.Flow.Core executors.
/// </summary>
internal sealed class McpSessionAdapter : IMcpSession
{
    private readonly McpClient _client;

    public McpSessionAdapter(string serverName, McpClient client)
    {
        ServerName = serverName;
        _client = client;
    }

    public string ServerName { get; }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new McpToolInfo
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                ? JsonNode.Parse(t.JsonSchema.GetRawText())
                : null
        }).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct)
    {
        // Skip if the server did not advertise resource capabilities
        if (_client.ServerCapabilities.Resources is null)
            return Array.Empty<McpResourceInfo>();

        var resources = await _client.ListResourcesAsync(cancellationToken: ct);
        return resources.Select(r => new McpResourceInfo
        {
            Uri = r.Uri,
            Name = r.Name,
            Description = r.Description,
            MimeType = r.MimeType
        }).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct)
    {
        // Skip if the server did not advertise prompt capabilities
        if (_client.ServerCapabilities.Prompts is null)
            return Array.Empty<McpPromptInfo>();

        var prompts = await _client.ListPromptsAsync(cancellationToken: ct);
        return prompts.Select(p => new McpPromptInfo
        {
            Name = p.Name,
            Description = p.Description,
            Arguments = p.ProtocolPrompt.Arguments?.Select(a => new McpPromptArgument
            {
                Name = a.Name,
                Description = a.Description,
                Required = a.Required == true
            }).ToList()
        }).ToList().AsReadOnly();
    }

    public async Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
    {
        var args = ConvertArguments(arguments);
        var result = await _client.CallToolAsync(toolName, args, cancellationToken: ct);

        return new McpCallResult
        {
            IsError = result.IsError == true,
            Content = BuildContent(result)
        };
    }

    public async Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
    {
        var args = ConvertArguments(arguments);
        var result = await _client.GetPromptAsync(promptName, args, cancellationToken: ct);

        return new McpGetPromptResult
        {
            Description = result.Description,
            Messages = result.Messages.Select(m => new McpPromptMessage
            {
                Role = m.Role.ToString().ToLowerInvariant(),
                Content = m.Content is TextContentBlock tc ? tc.Text ?? "" : m.Content?.ToString() ?? ""
            }).ToList()
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Helpers ──────────────────────────────────────────────────────

    private static Dictionary<string, object?>? ConvertArguments(JsonNode? arguments)
    {
        if (arguments is not JsonObject obj)
            return null;

        var dict = new Dictionary<string, object?>(obj.Count);
        foreach (var kv in obj)
        {
            dict[kv.Key] = ConvertArgumentValue(kv.Value);
        }
        return dict;
    }

    private static object? ConvertArgumentValue(JsonNode? value)
    {
        return value switch
        {
            null => null,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
            JsonValue jv when jv.TryGetValue<int>(out var i) => i,
            JsonValue jv when jv.TryGetValue<long>(out var l) => l,
            JsonValue jv when jv.TryGetValue<double>(out var d) => d,
            JsonArray arr => arr.Select(ConvertArgumentValue).ToList(),
            JsonObject obj => obj.ToDictionary(kvp => kvp.Key, kvp => ConvertArgumentValue(kvp.Value)),
            _ => value.ToJsonString()
        };
    }

    private static JsonNode? BuildContent(CallToolResult result)
    {
        if (result.Content is not { Count: > 0 })
            return null;

        // Single text block → try JSON parse, fallback to string
        if (result.Content.Count == 1 && result.Content[0] is TextContentBlock single)
        {
            var text = single.Text ?? "";
            try { return JsonNode.Parse(text); }
            catch { return text; }
        }

        // Multiple blocks → array
        var arr = new JsonArray();
        foreach (var block in result.Content)
        {
            arr.Add(block is TextContentBlock tb
                ? new JsonObject { ["type"] = "text", ["text"] = tb.Text }
                : new JsonObject { ["type"] = block.Type });
        }
        return arr;
    }
}
