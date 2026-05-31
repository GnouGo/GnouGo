using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
    private const string ProgressEnvelopeMarker = "gnougo.mcp.progress";
    private static readonly AsyncLocal<McpCorrelationContext?> CurrentCorrelation = new();
    private const int MaxCapturedStdioErrorLines = 80;
    private static readonly ConcurrentDictionary<string, StdioServerDiagnostics> StdioDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<McpRealtimeProgressEvent>>> ProgressHandlers = new(StringComparer.OrdinalIgnoreCase);

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
                Description = kv.Value.Description,
                DiscoveryTimeoutSeconds = kv.Value.DiscoveryTimeoutSeconds,
                CallTimeoutSeconds = kv.Value.CallTimeoutSeconds
            })
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<McpServerMetadata> ServerMetadata => _serverMetadata;

    public static IDisposable PushCorrelationContext(McpCorrelationContext context)
    {
        var previous = CurrentCorrelation.Value;
        CurrentCorrelation.Value = context;
        return new CorrelationScope(previous);
    }

    public static IDisposable PushProgressHandler(McpCorrelationContext context, Action<McpRealtimeProgressEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var registrationId = Guid.NewGuid();
        var keys = BuildProgressHandlerKeys(context).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var key in keys)
        {
            var handlers = ProgressHandlers.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Action<McpRealtimeProgressEvent>>());
            handlers[registrationId] = handler;
        }

        return new ProgressHandlerScope(registrationId, keys);
    }

    public static bool PublishProgress(McpRealtimeProgressEvent progressEvent)
    {
        var delivered = false;
        var deliveredHandlers = new HashSet<Guid>();

        foreach (var key in BuildProgressDispatchKeys(progressEvent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ProgressHandlers.TryGetValue(key, out var handlers))
                continue;

            foreach (var item in handlers)
            {
                if (!deliveredHandlers.Add(item.Key))
                    continue;

                try
                {
                    item.Value(progressEvent);
                    delivered = true;
                }
                catch
                {
                    // Progress callbacks must never break MCP stderr processing.
                }
            }
        }

        return delivered;
    }

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
            client = await CreateClientAsync(serverName, config, CurrentCorrelation.Value, ct);
            _clients.TryAdd(serverName, client);
        }

        return new McpSessionAdapter(serverName, client);
    }

    private static async Task<McpClient> CreateClientAsync(
        string serverName, McpServerOptions config, McpCorrelationContext? correlation, CancellationToken ct)
    {
        var type = config.Type?.ToLowerInvariant() ?? "http";

        IClientTransport transport = type switch
        {
            "http" or "sse" => CreateHttpTransport(config, correlation),
            "stdio" => CreateStdioTransport(serverName, config, correlation),
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.McpConnectionError,
                $"Unknown MCP transport type '{config.Type}' for server '{serverName}'")
        };

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "GnOuGo.Flow", Version = "1.0.0" }
        }, cancellationToken: ct);
    }

    private static HttpClientTransport CreateHttpTransport(McpServerOptions config, McpCorrelationContext? correlation)
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

        AddCorrelationHeaders(httpClient.DefaultRequestHeaders, correlation);

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

    private static StdioClientTransport CreateStdioTransport(string serverName, McpServerOptions config, McpCorrelationContext? correlation)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            throw new WorkflowRuntimeException(
                ErrorCodes.McpConnectionError,
                "MCP stdio transport requires a 'Command'");

        var commandResolution = ResolveStdioCommand(config.Command);
        var diagnostics = StdioDiagnostics.GetOrAdd(serverName, _ => new StdioServerDiagnostics());
        diagnostics.Reset(config.Command, commandResolution.Command, config.Args ?? [], commandResolution.WorkingDirectory);

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = commandResolution.Command,
            Arguments = config.Args ?? [],
            Name = "GnOuGo.Flow",
            WorkingDirectory = commandResolution.WorkingDirectory,
            EnvironmentVariables = BuildCorrelationEnvironment(correlation),
            StandardErrorLines = line => CaptureStdioErrorLine(serverName, line)
        });
    }

    internal static string FormatMcpFailureDiagnostics(string serverName, Exception exception)
    {
        var builder = new StringBuilder();
        builder.Append(exception.Message);

        var exceptionDetails = BuildExceptionChain(exception);
        if (!string.IsNullOrWhiteSpace(exceptionDetails) && !string.Equals(exceptionDetails, exception.Message, StringComparison.Ordinal))
            builder.Append(" Exception chain: ").Append(exceptionDetails);

        if (StdioDiagnostics.TryGetValue(serverName, out var diagnostics))
        {
            var launch = diagnostics.GetLaunchSummary();
            if (!string.IsNullOrWhiteSpace(launch))
                builder.Append(" Stdio launch: ").Append(launch);

            var stderrTail = diagnostics.GetStandardErrorTail();
            if (!string.IsNullOrWhiteSpace(stderrTail))
                builder.Append(" Stderr tail: ").Append(stderrTail);
        }

        return builder.ToString();
    }

    private static void CaptureStdioErrorLine(string serverName, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (TryParseProgressLine(serverName, line, out var progressEvent))
        {
            PublishProgress(progressEvent);
            return;
        }

        StdioDiagnostics.GetOrAdd(serverName, _ => new StdioServerDiagnostics()).AppendStandardError(line);
    }

    private static bool TryParseProgressLine(string serverName, string line, out McpRealtimeProgressEvent progressEvent)
    {
        progressEvent = default!;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{'))
            return false;

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(trimmed) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (obj is null)
            return false;

        var type = GetStringProperty(obj, "type") ?? GetStringProperty(obj, "$type");
        var marker = GetStringProperty(obj, "gnougo") ?? GetStringProperty(obj, "marker");
        if (!string.Equals(type, ProgressEnvelopeMarker, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(marker, ProgressEnvelopeMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var eventObj = GetObjectProperty(obj, "event") ?? obj;
        var message = GetStringProperty(eventObj, "message");
        if (string.IsNullOrWhiteSpace(message))
            return false;

        progressEvent = new McpRealtimeProgressEvent
        {
            ServerName = GetStringProperty(obj, "server") ?? GetStringProperty(obj, "mcpServer") ?? serverName,
            MethodName = GetStringProperty(obj, "method") ?? GetStringProperty(obj, "mcpMethod"),
            Kind = GetStringProperty(obj, "kind") ?? GetStringProperty(obj, "mcpKind"),
            CorrelationId = GetStringProperty(obj, "correlationId"),
            RunId = GetStringProperty(obj, "runId"),
            StepId = GetStringProperty(obj, "stepId"),
            StepType = GetStringProperty(obj, "stepType"),
            EventKind = GetStringProperty(eventObj, "kind"),
            Level = GetStringProperty(eventObj, "level"),
            Message = message,
            File = GetStringProperty(eventObj, "file"),
            Timestamp = GetStringProperty(eventObj, "timestamp")
        };
        return true;
    }

    private static IEnumerable<string> BuildProgressHandlerKeys(McpCorrelationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.CorrelationId))
            yield return "correlation:" + context.CorrelationId;
        if (!string.IsNullOrWhiteSpace(context.RunId) && !string.IsNullOrWhiteSpace(context.StepId))
            yield return $"run-step:{context.RunId}:{context.StepId}";
        if (!string.IsNullOrWhiteSpace(context.ServerName) && !string.IsNullOrWhiteSpace(context.MethodName))
            yield return $"server-method:{context.ServerName}:{context.MethodName}";
        if (!string.IsNullOrWhiteSpace(context.ServerName))
            yield return "server:" + context.ServerName;
    }

    private static IEnumerable<string> BuildProgressDispatchKeys(McpRealtimeProgressEvent progressEvent)
    {
        if (!string.IsNullOrWhiteSpace(progressEvent.CorrelationId))
            yield return "correlation:" + progressEvent.CorrelationId;
        if (!string.IsNullOrWhiteSpace(progressEvent.RunId) && !string.IsNullOrWhiteSpace(progressEvent.StepId))
            yield return $"run-step:{progressEvent.RunId}:{progressEvent.StepId}";
        if (!string.IsNullOrWhiteSpace(progressEvent.ServerName) && !string.IsNullOrWhiteSpace(progressEvent.MethodName))
            yield return $"server-method:{progressEvent.ServerName}:{progressEvent.MethodName}";
        if (!string.IsNullOrWhiteSpace(progressEvent.ServerName))
            yield return "server:" + progressEvent.ServerName;
    }

    private static JsonObject? GetObjectProperty(JsonObject obj, string name)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase) && property.Value is JsonObject value)
                return value;
        }

        return null;
    }

    private static string? GetStringProperty(JsonObject obj, string name)
    {
        foreach (var property in obj)
        {
            if (!string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                return text;

            return property.Value?.ToJsonString().Trim('"');
        }

        return null;
    }

    private static string BuildExceptionChain(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var typeName = current.GetType().FullName ?? current.GetType().Name;
            parts.Add($"{typeName}: {current.Message}");
        }

        return string.Join(" -> ", parts);
    }

    internal static string? ResolveStdioWorkingDirectory(string command)
        => ResolveStdioCommand(command).WorkingDirectory;

    internal static StdioCommandResolution ResolveStdioCommand(string command)
        => ResolveStdioCommand(command, AppContext.BaseDirectory);

    internal static StdioCommandResolution ResolveStdioCommand(string command, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new StdioCommandResolution(command, null);

        var normalizedCommand = command.Replace('/', Path.DirectorySeparatorChar)
                                       .Replace('\\', Path.DirectorySeparatorChar);
        if (!LooksLikeFileSystemCommand(normalizedCommand))
            return new StdioCommandResolution(command, null);

        var commandPath = Path.IsPathRooted(normalizedCommand)
            ? normalizedCommand
            : Path.GetFullPath(Path.Combine(baseDirectory, normalizedCommand));

        var resolvedCommandPath = ResolveExistingExecutablePath(commandPath) ?? commandPath;
        var workingDirectory = File.Exists(resolvedCommandPath)
            ? Path.GetDirectoryName(resolvedCommandPath)
            : null;

        return new StdioCommandResolution(resolvedCommandPath, workingDirectory);
    }

    private static bool LooksLikeFileSystemCommand(string command)
        => Path.IsPathRooted(command)
           || command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
           || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
               && command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal));

    private static string? ResolveExistingExecutablePath(string commandPath)
    {
        if (File.Exists(commandPath))
            return commandPath;

        if (!OperatingSystem.IsWindows() || commandPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var windowsExecutablePath = commandPath + ".exe";
        return File.Exists(windowsExecutablePath) ? windowsExecutablePath : null;
    }

    internal readonly record struct StdioCommandResolution(string Command, string? WorkingDirectory);

    private static void AddCorrelationHeaders(HttpRequestHeaders headers, McpCorrelationContext? correlation)
    {
        if (correlation == null)
            return;

        AddHeader(headers, "x-gnougo-correlation-id", correlation.CorrelationId);
        AddHeader(headers, "x-gnougo-run-id", correlation.RunId);
        AddHeader(headers, "x-gnougo-step-id", correlation.StepId);
        AddHeader(headers, "x-gnougo-step-type", correlation.StepType);
        AddHeader(headers, "x-gnougo-mcp-server", correlation.ServerName);
        AddHeader(headers, "x-gnougo-mcp-method", correlation.MethodName);
        AddHeader(headers, "x-gnougo-mcp-kind", correlation.Kind);
        AddHeader(headers, "traceparent", correlation.TraceParent);
    }

    private static void AddHeader(HttpRequestHeaders headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            headers.TryAddWithoutValidation(name, value);
    }

    private static Dictionary<string, string?>? BuildCorrelationEnvironment(McpCorrelationContext? correlation)
    {
        if (correlation == null)
            return null;

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddEnv(env, "GNouGo__CorrelationId", correlation.CorrelationId);
        AddEnv(env, "GNouGo__RunId", correlation.RunId);
        AddEnv(env, "GNouGo__TraceId", correlation.TraceId);
        AddEnv(env, "GNouGo__SpanId", correlation.SpanId);
        AddEnv(env, "GNouGo__TraceParent", correlation.TraceParent);
        AddEnv(env, "GNouGo__StepId", correlation.StepId);
        AddEnv(env, "GNouGo__StepType", correlation.StepType);
        AddEnv(env, "GNouGo__McpServer", correlation.ServerName);
        AddEnv(env, "GNouGo__McpMethod", correlation.MethodName);
        AddEnv(env, "GNouGo__McpKind", correlation.Kind);
        return env.Count == 0 ? null : env;
    }

    internal static JsonObject? BuildCurrentCorrelationMeta()
    {
        var correlation = CurrentCorrelation.Value;
        var activity = System.Diagnostics.Activity.Current;

        if (correlation is null && activity is null)
            return null;

        var gnougo = new JsonObject();
        AddJson(gnougo, "correlationId", correlation?.CorrelationId);
        AddJson(gnougo, "runId", correlation?.RunId);
        AddJson(gnougo, "traceId", activity?.TraceId.ToString() ?? correlation?.TraceId);
        AddJson(gnougo, "spanId", activity?.SpanId.ToString() ?? correlation?.SpanId);
        AddJson(gnougo, "parentSpanId", activity?.ParentSpanId.ToString() ?? correlation?.SpanId);
        AddJson(gnougo, "traceparent", activity?.Id ?? correlation?.TraceParent);
        AddJson(gnougo, "tracestate", activity?.TraceStateString);
        AddJson(gnougo, "stepId", correlation?.StepId);
        AddJson(gnougo, "stepType", correlation?.StepType);
        AddJson(gnougo, "mcpServer", correlation?.ServerName);
        AddJson(gnougo, "mcpMethod", correlation?.MethodName);
        AddJson(gnougo, "mcpKind", correlation?.Kind);

        if (gnougo.Count == 0)
            return null;

        var meta = new JsonObject { ["gnougo"] = gnougo };
        AddJson(meta, "traceparent", activity?.Id ?? correlation?.TraceParent);
        AddJson(meta, "tracestate", activity?.TraceStateString);
        return meta;
    }

    private static void AddEnv(Dictionary<string, string?> env, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            env[name] = value;
    }

    private static void AddJson(JsonObject obj, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            obj[name] = value;
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

    private sealed class CorrelationScope : IDisposable
    {
        private readonly McpCorrelationContext? _previous;
        private bool _disposed;

        public CorrelationScope(McpCorrelationContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentCorrelation.Value = _previous;
            _disposed = true;
        }
    }

    private sealed class ProgressHandlerScope : IDisposable
    {
        private readonly Guid _registrationId;
        private readonly IReadOnlyList<string> _keys;
        private bool _disposed;

        public ProgressHandlerScope(Guid registrationId, IReadOnlyList<string> keys)
        {
            _registrationId = registrationId;
            _keys = keys;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (var key in _keys)
            {
                if (!ProgressHandlers.TryGetValue(key, out var handlers))
                    continue;

                handlers.TryRemove(_registrationId, out _);
                if (handlers.IsEmpty)
                    ProgressHandlers.TryRemove(key, out _);
            }

            _disposed = true;
        }
    }

    private sealed class StdioServerDiagnostics
    {
        private readonly object _gate = new();
        private readonly Queue<string> _stderrLines = new(MaxCapturedStdioErrorLines);
        private string? _configuredCommand;
        private string? _command;
        private IReadOnlyList<string> _arguments = [];
        private string? _workingDirectory;

        public void Reset(string configuredCommand, string command, IReadOnlyList<string> arguments, string? workingDirectory)
        {
            lock (_gate)
            {
                _configuredCommand = configuredCommand;
                _command = command;
                _arguments = arguments.ToArray();
                _workingDirectory = workingDirectory;
                _stderrLines.Clear();
            }
        }

        public void AppendStandardError(string line)
        {
            lock (_gate)
            {
                if (_stderrLines.Count == MaxCapturedStdioErrorLines)
                    _stderrLines.Dequeue();

                _stderrLines.Enqueue(line.TrimEnd());
            }
        }

        public string GetLaunchSummary()
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_command))
                    return string.Empty;

                var args = BuildArgumentsSummary(_arguments);
                var configuredCommand = string.IsNullOrWhiteSpace(_configuredCommand) ? "<null>" : _configuredCommand;
                var workingDirectory = string.IsNullOrWhiteSpace(_workingDirectory) ? "<null>" : _workingDirectory;
                return string.Equals(_configuredCommand, _command, StringComparison.Ordinal)
                    ? $"command={QuoteArgument(_command)}, args={args}, workingDirectory={workingDirectory}"
                    : $"configuredCommand={QuoteArgument(configuredCommand)}, command={QuoteArgument(_command)}, args={args}, workingDirectory={workingDirectory}";
            }
        }

        public string GetStandardErrorTail()
        {
            lock (_gate)
            {
                return _stderrLines.Count == 0
                    ? string.Empty
                    : string.Join(" | ", _stderrLines);
            }
        }

        private static string QuoteArgument(string value)
            => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

        private static string BuildArgumentsSummary(IReadOnlyList<string> arguments)
        {
            if (arguments.Count == 0)
                return "<none>";

            var builder = new StringBuilder();
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                builder.Append(QuoteArgument(arguments[i]));
            }

            return builder.ToString();
        }
    }
}

public sealed class McpRealtimeProgressEvent
{
    public string? ServerName { get; init; }
    public string? MethodName { get; init; }
    public string? Kind { get; init; }
    public string? CorrelationId { get; init; }
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public string? EventKind { get; init; }
    public string? Level { get; init; }
    public string Message { get; init; } = "";
    public string? File { get; init; }
    public string? Timestamp { get; init; }
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
            arr.Add((JsonNode)(block is TextContentBlock tb
                ? new JsonObject { ["type"] = "text", ["text"] = tb.Text }
                : new JsonObject { ["type"] = block.Type }));
        }
        return arr;
    }
}
