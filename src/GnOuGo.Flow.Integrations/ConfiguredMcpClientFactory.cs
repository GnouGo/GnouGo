using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using McpArtifactContractParser = GnOuGo.Mcp.Core.McpArtifactContractParser;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Real <see cref="IMcpClientFactory"/> implementation that connects to MCP servers
/// using the Microsoft ModelContextProtocol 2.x library.
/// Reads configuration from a dictionary of <see cref="McpServerOptions"/>.
/// Shared by both GnOuGo.Flow.Cli and GnOuGo.Flow.Server.
/// </summary>
public sealed class ConfiguredMcpClientFactory : IMcpClientFactory, IMcpExecutionHooks, IAsyncDisposable
{
    private const string ProgressEnvelopeMarker = "gnougo.mcp.progress";
    private static readonly AsyncLocal<McpCorrelationContext?> CurrentCorrelation = new();
    private const int MaxCapturedStdioErrorLines = 80;
    private static readonly ConcurrentDictionary<string, StdioServerDiagnostics> StdioDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<McpRealtimeProgressEvent>>> ProgressHandlers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<McpHumanInputSignal>>> HumanInputHandlers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Guid, McpCorrelationContext> ActiveHumanInputCalls = new();

    private readonly Dictionary<string, McpServerOptions> _serverConfigs;
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, McpSessionAdapter> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientCreationGates = new();
    private readonly IReadOnlyList<McpServerMetadata> _serverMetadata;
    private readonly IHumanInputProvider? _humanInputProvider;
    private readonly string? _defaultLlmProvider;
    private readonly string? _defaultLlmModel;

    public ConfiguredMcpClientFactory(
        Dictionary<string, McpServerOptions> serverConfigs,
        IHumanInputProvider? humanInputProvider = null,
        string? defaultLlmProvider = null,
        string? defaultLlmModel = null)
    {
        _serverConfigs = serverConfigs;
        _humanInputProvider = humanInputProvider;
        _defaultLlmProvider = string.IsNullOrWhiteSpace(defaultLlmProvider) ? null : defaultLlmProvider.Trim();
        _defaultLlmModel = string.IsNullOrWhiteSpace(defaultLlmModel) ? null : defaultLlmModel.Trim();
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

    IDisposable IMcpExecutionHooks.BeginCall(McpCallExecutionContext context)
        => new McpExecutionScope(
            PushCorrelationContext(context.Correlation),
            PushProgressHandler(context.Correlation, context.ProgressHandler),
            PushHumanInputHandler(context.Correlation, context.HumanInputHandler));

    string IMcpExecutionHooks.FormatFailureDiagnostics(string serverName, Exception exception)
        => FormatMcpFailureDiagnostics(serverName, exception);

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

    public static IDisposable PushHumanInputHandler(McpCorrelationContext context, Action<McpHumanInputSignal> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var registrationId = Guid.NewGuid();
        ActiveHumanInputCalls[registrationId] = context;
        var keys = BuildProgressHandlerKeys(context).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var key in keys)
        {
            var handlers = HumanInputHandlers.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Action<McpHumanInputSignal>>());
            handlers[registrationId] = handler;
        }

        return new HumanInputHandlerScope(registrationId, keys);
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

    public static bool PublishHumanInput(McpHumanInputSignal signal)
    {
        // Dispatch through the most specific available identity only. Falling through
        // to server/method keys after an exact run/step match would let concurrent
        // calls to the same cached MCP client observe each other's elicitation.
        foreach (var key in BuildProgressHandlerKeys(signal.Correlation).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!HumanInputHandlers.TryGetValue(key, out var handlers))
                continue;

            var delivered = false;
            foreach (var item in handlers)
            {
                try
                {
                    item.Value(signal);
                    delivered = true;
                }
                catch
                {
                    // Human-input telemetry must never break the elicitation exchange.
                }
            }

            return delivered;
        }

        return false;
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
            var gate = _clientCreationGates.GetOrAdd(serverName, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!_clients.TryGetValue(serverName, out client))
                {
                    client = await CreateClientAsync(serverName, config, CurrentCorrelation.Value, ct);
                    _clients[serverName] = client;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return _sessions.GetOrAdd(serverName, _ => new McpSessionAdapter(serverName, client));
    }

    private async Task<McpClient> CreateClientAsync(
        string serverName, McpServerOptions config, McpCorrelationContext? correlation, CancellationToken ct)
    {
        var type = config.Type?.ToLowerInvariant() ?? "http";

        IClientTransport transport = type switch
        {
            "http" or "sse" => CreateHttpTransport(config, correlation),
            "stdio" => CreateStdioTransport(serverName, config, correlation, _defaultLlmProvider, _defaultLlmModel),
            _ => throw new WorkflowRuntimeException(
                ErrorCodes.McpConnectionError,
                $"Unknown MCP transport type '{config.Type}' for server '{serverName}'")
        };

        return await McpClient.CreateAsync(transport, CreateClientOptions(_humanInputProvider, serverName), cancellationToken: ct);
    }

    internal static McpClientOptions CreateClientOptions(
        IHumanInputProvider? humanInputProvider = null,
        string? serverName = null)
    {
        var options = new McpClientOptions
        {
        // Leave ProtocolVersion unset so SDK 2.x prefers 2026-07-28 discovery and
        // automatically falls back to initialize-handshake servers when required.
            ClientInfo = new Implementation { Name = "GnOuGo.Flow", Version = "1.0.0" }
        };
        if (humanInputProvider is not null)
        {
            options.Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (request, cancellationToken) =>
                    await HandleElicitationAsync(humanInputProvider, serverName, request, cancellationToken)
            };
        }
        return options;
    }

    private static async ValueTask<ElicitResult> HandleElicitationAsync(
        IHumanInputProvider provider,
        string? serverName,
        ElicitRequestParams? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return new ElicitResult { Action = "cancel" };
        // Elicitation may be dispatched by a cached client's background transport
        // loop, where AsyncLocal state is not guaranteed to represent the active
        // call. Prefer the call metadata echoed by the MCP server.
        var correlation = ResolveHumanInputCorrelation(request.Meta);
        if (correlation is null && !string.IsNullOrWhiteSpace(serverName))
        {
            correlation = ResolveSoleActiveHumanInputCall(serverName, out var ambiguous);
            if (ambiguous)
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName}' requested human input without call correlation metadata while multiple calls were active. The request was rejected to prevent cross-run input delivery.");
            }
        }
        correlation ??= CurrentCorrelation.Value;
        var fields = request.RequestedSchema?.Properties.Select(property => new HumanInputFieldDef
        {
            Name = property.Key,
            Type = property.Value is ElicitRequestParams.UntitledSingleSelectEnumSchema or ElicitRequestParams.TitledSingleSelectEnumSchema ? "select" : "string",
            Required = request.RequestedSchema.Required?.Contains(property.Key, StringComparer.Ordinal) == true,
            Description = property.Value.Description,
            Options = property.Value switch
            {
                ElicitRequestParams.UntitledSingleSelectEnumSchema single => single.Enum?.ToList(),
                ElicitRequestParams.TitledSingleSelectEnumSchema titled => titled.OneOf?.Select(static item => item.Const).ToList(),
                _ => null
            }
        }).ToList();
        var humanRequest = new HumanInputRequest
        {
            RunId = correlation?.RunId ?? correlation?.CorrelationId ?? Guid.NewGuid().ToString("N"),
            StepId = correlation?.StepId ?? "mcp-elicitation",
            Prompt = request.Message,
            Mode = fields is { Count: > 1 } ? HumanInputContract.ModeForm : HumanInputContract.ModeChoice,
            Context = BuildMcpHumanInputContext(correlation),
            Fields = fields,
            Choices = fields is { Count: 1 } ? fields[0].Options : null,
            TimeoutMs = HumanInputContract.DefaultTimeoutMs
        };
        var effectiveCorrelation = correlation ?? new McpCorrelationContext
        {
            CorrelationId = humanRequest.RunId,
            RunId = humanRequest.RunId,
            StepId = humanRequest.StepId,
            StepType = "mcp.call"
        };
        PublishHumanInput(new McpHumanInputSignal(effectiveCorrelation, humanRequest, McpHumanInputSignalPhase.Waiting));

        JsonNode? response;
        try
        {
            response = await provider.RequestInputAsync(humanRequest, cancellationToken);
        }
        catch
        {
            PublishHumanInput(new McpHumanInputSignal(effectiveCorrelation, humanRequest, McpHumanInputSignalPhase.Cancelled));
            throw;
        }

        if (response is null)
        {
            PublishHumanInput(new McpHumanInputSignal(effectiveCorrelation, humanRequest, McpHumanInputSignalPhase.Cancelled));
            return new ElicitResult { Action = "cancel" };
        }

        PublishHumanInput(new McpHumanInputSignal(
            effectiveCorrelation,
            humanRequest,
            IsRefusalResponse(response) ? McpHumanInputSignalPhase.Refused : McpHumanInputSignalPhase.Resumed));

        var content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (response is JsonObject responseObject)
        {
            foreach (var field in fields ?? [])
            {
                var value = responseObject[field.Name];
                if (value is null
                    && fields is { Count: 1 })
                {
                    // Choice-oriented human-input providers use the stable
                    // { "response": ... } envelope. Preserve the requested
                    // MCP schema name when bridging that optimized UI shape.
                    value = responseObject["response"];
                }

                if (value is not null)
                    content[field.Name] = JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
            }
        }
        else if (fields is { Count: 1 })
        {
            content[fields[0].Name] = JsonDocument.Parse(response.ToJsonString()).RootElement.Clone();
        }
        return new ElicitResult { Action = "accept", Content = content };
    }

    private static JsonObject? BuildMcpHumanInputContext(McpCorrelationContext? correlation)
    {
        if (correlation is null)
            return null;

        var context = new JsonObject();
        if (!string.IsNullOrWhiteSpace(correlation.ServerName))
            context["mcp_server"] = correlation.ServerName;
        if (!string.IsNullOrWhiteSpace(correlation.MethodName))
            context["mcp_method"] = correlation.MethodName;
        if (!string.IsNullOrWhiteSpace(correlation.Kind))
            context["mcp_kind"] = correlation.Kind;
        if (!string.IsNullOrWhiteSpace(correlation.ExecutionId))
            context["execution_id"] = correlation.ExecutionId;
        if (!string.IsNullOrWhiteSpace(correlation.AgentId))
            context["agent_id"] = correlation.AgentId;
        if (!string.IsNullOrWhiteSpace(correlation.AgentName))
            context["agent_name"] = correlation.AgentName;
        return context.Count == 0 ? null : context;
    }

    private static McpCorrelationContext? ResolveHumanInputCorrelation(JsonObject? meta)
    {
        if (meta is null)
            return null;

        var gnougo = meta["gnougo"] as JsonObject;
        if (gnougo is null)
            return null;

        var correlation = new McpCorrelationContext
        {
            CorrelationId = ReadMetaString(gnougo, "correlationId"),
            TenantId = ReadMetaString(gnougo, "tenantId"),
            RunId = ReadMetaString(gnougo, "runId"),
            ExecutionId = ReadMetaString(gnougo, "executionId"),
            AgentId = ReadMetaString(gnougo, "agentId"),
            AgentName = ReadMetaString(gnougo, "agentName"),
            TraceId = ReadMetaString(gnougo, "traceId"),
            SpanId = ReadMetaString(gnougo, "spanId"),
            TraceParent = ReadMetaString(gnougo, "traceparent") ?? ReadMetaString(meta, "traceparent"),
            StepId = ReadMetaString(gnougo, "stepId"),
            StepType = ReadMetaString(gnougo, "stepType"),
            ServerName = ReadMetaString(gnougo, "mcpServer"),
            MethodName = ReadMetaString(gnougo, "mcpMethod"),
            Kind = ReadMetaString(gnougo, "mcpKind"),
            Context = gnougo["context"]?.DeepClone() as JsonObject
        };

        return string.IsNullOrWhiteSpace(correlation.CorrelationId)
               && string.IsNullOrWhiteSpace(correlation.RunId)
               && string.IsNullOrWhiteSpace(correlation.StepId)
            ? null
            : correlation;
    }

    private static McpCorrelationContext? ResolveSoleActiveHumanInputCall(
        string serverName,
        out bool ambiguous)
    {
        var matches = ActiveHumanInputCalls.Values
            .Where(context => string.Equals(context.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                static context => string.Join(
                    "\u001f",
                    context.CorrelationId ?? string.Empty,
                    context.RunId ?? string.Empty,
                    context.StepId ?? string.Empty),
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .Take(2)
            .ToArray();
        ambiguous = matches.Length > 1;
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? ReadMetaString(JsonObject source, string name)
        => source.TryGetPropertyValue(name, out var node)
           && node is JsonValue value
           && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool IsRefusalResponse(JsonNode response)
    {
        var candidate = response;
        if (response is JsonObject obj)
            candidate = obj["response"] ?? obj["answer"] ?? obj["decision"] ?? response;

        return candidate is JsonValue value
               && value.TryGetValue<string>(out var text)
               && text.Trim().ToLowerInvariant() is "deny" or "refuse" or "reject" or "cancel";
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

    private static StdioClientTransport CreateStdioTransport(
        string serverName,
        McpServerOptions config,
        McpCorrelationContext? correlation,
        string? defaultLlmProvider,
        string? defaultLlmModel)
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
            EnvironmentVariables = BuildStdioEnvironment(config, correlation, defaultLlmProvider, defaultLlmModel),
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
        AddHeader(headers, "x-gnougo-tenant-id", correlation.TenantId);
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

    private static Dictionary<string, string?>? BuildStdioEnvironment(
        McpServerOptions config,
        McpCorrelationContext? correlation,
        string? defaultLlmProvider,
        string? defaultLlmModel)
    {
        var env = BuildCorrelationEnvironment(correlation) ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddEnv(env, "GNouGo__DefaultLlmProvider", defaultLlmProvider);
        AddEnv(env, "GNouGo__DefaultLlmModel", defaultLlmModel);
        if (config.EnvironmentVariables is not null)
        {
            foreach (var kv in config.EnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    env[kv.Key] = kv.Value;
            }
        }

        return env.Count == 0 ? null : env;
    }

    private static Dictionary<string, string?>? BuildCorrelationEnvironment(McpCorrelationContext? correlation)
    {
        if (correlation == null)
            return null;

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddEnv(env, "GNouGo__CorrelationId", correlation.CorrelationId);
        AddEnv(env, "GNouGo__TenantId", correlation.TenantId);
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
        AddJson(gnougo, "tenantId", correlation?.TenantId);
        AddJson(gnougo, "runId", correlation?.RunId);
        AddJson(gnougo, "executionId", correlation?.ExecutionId);
        AddJson(gnougo, "agentId", correlation?.AgentId);
        AddJson(gnougo, "agentName", correlation?.AgentName);
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
        if (correlation?.Context is { Count: > 0 })
            gnougo["context"] = correlation.Context.DeepClone();

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
        _sessions.Clear();
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();
        _clientCreationGates.Clear();
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

    private sealed class McpExecutionScope : IDisposable
    {
        private readonly IDisposable _correlationScope;
        private readonly IDisposable _progressScope;
        private readonly IDisposable _humanInputScope;

        public McpExecutionScope(
            IDisposable correlationScope,
            IDisposable progressScope,
            IDisposable humanInputScope)
        {
            _correlationScope = correlationScope;
            _progressScope = progressScope;
            _humanInputScope = humanInputScope;
        }

        public void Dispose()
        {
            _humanInputScope.Dispose();
            _progressScope.Dispose();
            _correlationScope.Dispose();
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

    private sealed class HumanInputHandlerScope : IDisposable
    {
        private readonly Guid _registrationId;
        private readonly IReadOnlyList<string> _keys;
        private bool _disposed;

        public HumanInputHandlerScope(Guid registrationId, IReadOnlyList<string> keys)
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
                if (!HumanInputHandlers.TryGetValue(key, out var handlers))
                    continue;

                handlers.TryRemove(_registrationId, out _);
                if (handlers.IsEmpty)
                    HumanInputHandlers.TryRemove(key, out _);
            }

            ActiveHumanInputCalls.TryRemove(_registrationId, out _);
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

/// <summary>
/// Adapts a <see cref="McpClient"/> from the Microsoft library
/// to the <see cref="IMcpSession"/> interface used by GnOuGo.Flow.Core executors.
/// </summary>
internal sealed class McpSessionAdapter : IMcpSession, ILiveMcpToolDiscoverySession
{
    private readonly McpClient _client;
    private readonly SemaphoreSlim _toolDiscoveryGate = new(1, 1);
    private IReadOnlyList<McpToolInfo>? _discoveredTools;

    public McpSessionAdapter(string serverName, McpClient client)
    {
        ServerName = serverName;
        _client = client;
    }

    public string ServerName { get; }

    public async Task<IReadOnlyList<McpToolInfo>> EnsureToolsDiscoveredAsync(CancellationToken ct)
    {
        if (_discoveredTools is not null)
            return _discoveredTools;

        await _toolDiscoveryGate.WaitAsync(ct);
        try
        {
            return _discoveredTools ??= await ListToolsCoreAsync(ct);
        }
        finally
        {
            _toolDiscoveryGate.Release();
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = await ListToolsCoreAsync(ct);
        _discoveredTools = tools;
        return tools;
    }

    private async Task<IReadOnlyList<McpToolInfo>> ListToolsCoreAsync(CancellationToken ct)
    {
        var tools = await _client.ListToolsAsync(CreateRequestOptions(), ct);
        var mappedTools = tools.Select(t =>
        {
            var mapped = new McpToolInfo
            {
                Name = t.Name,
                Description = t.Description,
                Meta = t.ProtocolTool.Meta?.DeepClone(),
                InputSchema = t.JsonSchema.ValueKind != JsonValueKind.Undefined
                    ? JsonNode.Parse(t.JsonSchema.GetRawText())
                    : null,
                OutputSchema = t.ReturnJsonSchema.HasValue
                    ? JsonNode.Parse(t.ReturnJsonSchema.Value.GetRawText())
                    : null
            };
            mapped.ArtifactContract = ResolveArtifactContract(mapped);
            return mapped;
        }).ToList().AsReadOnly();
        return McpToolContractEnricher.EnrichTools(mappedTools);
    }

    private static McpArtifactContractResolution? ResolveArtifactContract(McpToolInfo tool)
    {
        var validation = McpArtifactContractParser.ParseAndValidate(
            tool.Meta,
            tool.InputSchema,
            tool.OutputSchema);
        if (!validation.IsDeclared)
            return null;

        var contract = validation.Contract == null
            ? null
            : new GnOuGo.Flow.Core.Runtime.McpArtifactContract(
                validation.Contract.Version,
                validation.Contract.Produces
                    .Select(static artifact => new GnOuGo.Flow.Core.Runtime.McpProducedArtifact(
                        artifact.Kind,
                        artifact.Pointer,
                        artifact.Mode))
                    .ToArray(),
                validation.Contract.Consumes
                    .Select(static artifact => new GnOuGo.Flow.Core.Runtime.McpConsumedArtifact(
                        artifact.Kind,
                        artifact.Pointer,
                        artifact.Required))
                    .ToArray());
        return new McpArtifactContractResolution(contract, validation.Errors);
    }

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct)
    {
        // Skip if the server did not advertise resource capabilities
        if (_client.ServerCapabilities.Resources is null)
            return Array.Empty<McpResourceInfo>();

        var resources = await _client.ListResourcesAsync(CreateRequestOptions(), ct);
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

        var prompts = await _client.ListPromptsAsync(CreateRequestOptions(), ct);
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
        var result = await _client.CallToolAsync(toolName, args, progress: null, CreateRequestOptions(), ct);

        return new McpCallResult
        {
            IsError = result.IsError == true,
            Content = BuildContent(result)
        };
    }

    public async Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct)
    {
        var args = ConvertArguments(arguments);
        var result = await _client.GetPromptAsync(promptName, args, CreateRequestOptions(), ct);

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

    private static RequestOptions CreateRequestOptions()
        => new() { Meta = ConfiguredMcpClientFactory.BuildCurrentCorrelationMeta() };

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
        if (result.StructuredContent is JsonElement structuredContent
            && structuredContent.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            return JsonNode.Parse(structuredContent.GetRawText());
        }

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
