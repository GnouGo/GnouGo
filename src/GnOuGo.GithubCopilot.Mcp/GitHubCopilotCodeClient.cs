using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class GitHubCopilotCodeClient : ICodeAssistantClient
{
    private readonly CodeServerSettings _settings;
    private readonly CodePolicy _policy;
    private readonly ICopilotProviderConfigResolver _providerConfigResolver;
    private readonly CodeMcpTraceContextAccessor _traceContextAccessor;
    private readonly ILogger<GitHubCopilotCodeClient> _logger;

    public GitHubCopilotCodeClient(
        IOptions<CodeServerSettings> settings,
        CodePolicy policy,
        ICopilotProviderConfigResolver providerConfigResolver,
        CodeMcpTraceContextAccessor traceContextAccessor,
        ILogger<GitHubCopilotCodeClient> logger)
    {
        _settings = settings.Value;
        _policy = policy;
        _providerConfigResolver = providerConfigResolver;
        _traceContextAccessor = traceContextAccessor;
        _logger = logger;
    }

    public async Task<CodeSuggestionResult> SuggestChangeAsync(
        string task,
        string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles,
        string? providerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new InvalidOperationException("task must not be empty.");
        _policy.EnsurePromptWithinLimit(task, nameof(task));

        var prompt = BuildPrompt(task, projectRoot, contextFiles);

        var token = _policy.ResolveConfiguredToken();
        if (!_settings.Copilot.UseLoggedInUser && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "A GitHub token is required unless Code:Copilot:UseLoggedInUser=true. " +
                "For Copilot SDK authentication, use a locally signed-in GitHub account or configure Code:Copilot:ApiKey / token environment variables.");
        }

        using var activity = StartCopilotActivity(_settings, _traceContextAccessor);
        var providerOverride = await _providerConfigResolver.ResolveAsync(
            providerName,
            _settings.Copilot.Model,
            token,
            cancellationToken);
        var model = providerOverride?.Model ?? _settings.Copilot.Model;

        await using var client = CreateClient(projectRoot, token);
        await client.StartAsync(cancellationToken);

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            ClientName = "GnOuGo.GithubCopilot.Mcp",
            Model = model,
            ReasoningEffort = NormalizeNullable(_settings.Copilot.ReasoningEffort),
            Provider = providerOverride?.Provider,
            WorkingDirectory = projectRoot,
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll
        }, cancellationToken);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.Copilot.RequestTimeoutSeconds));
        var response = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = prompt,
            Mode = NormalizeMessageMode(_settings.Copilot.Mode),
            RequestHeaders = BuildRequestHeaders(_settings, _traceContextAccessor)
        }, timeout, cancellationToken);

        var data = response?.Data;
        var suggestion = data?.Content;
        if (string.IsNullOrWhiteSpace(suggestion))
            throw new InvalidOperationException("GitHub Copilot returned an empty response.");

        _logger.LogDebug(
            "GitHub Copilot SDK completed code suggestion for {FileCount} context files using provider {Provider} and model {Model}.",
            contextFiles.Count,
            providerOverride?.ProviderName ?? _settings.Copilot.Provider,
            model);

        return new CodeSuggestionResult(
            Task: task,
            Files: contextFiles.Select(static file => file.Path).ToArray(),
            Suggestion: suggestion,
            Model: model,
            UsageJson: data is null ? null : BuildUsageJson(data));
    }

    internal CopilotClient CreateClient(string projectRoot, string? token)
        => new(BuildClientOptions(_settings, projectRoot, token, _logger));

    internal static CopilotClientOptions BuildClientOptions(
        CodeServerSettings settings,
        string projectRoot,
        string? token,
        ILogger? logger = null)
    {
        if (!settings.Copilot.UseLoggedInUser && string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A GitHub token is required when Code:Copilot:UseLoggedInUser=false.", nameof(token));

        return new CopilotClientOptions
        {
            Cwd = projectRoot,
            GitHubToken = string.IsNullOrWhiteSpace(token) ? null : token,
            UseLoggedInUser = settings.Copilot.UseLoggedInUser,
            LogLevel = NormalizeLogLevel(settings.Copilot.LogLevel),
            Environment = BuildClientEnvironment(settings),
            Telemetry = BuildTelemetryConfig(settings.Copilot.Telemetry),
            Logger = logger
        };
    }

    public static string NormalizeMessageMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "ask";

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized switch
        {
            "plan" => "ask",
            "ask" or "edit" or "agent" => normalized,
            _ => throw new InvalidOperationException($"Unsupported Copilot mode '{mode}'. Supported modes: ask, edit, agent. Legacy alias: plan -> ask.")
        };
    }

    internal static string NormalizeLogLevel(string? logLevel)
    {
        if (string.IsNullOrWhiteSpace(logLevel))
            return "warning";

        return logLevel.Trim().ToLowerInvariant() switch
        {
            "warn" => "warning",
            "trace" => "all",
            "none" or "error" or "warning" or "info" or "debug" or "all" or "default" => logLevel.Trim().ToLowerInvariant(),
            _ => throw new InvalidOperationException(
                $"Unsupported Copilot log level '{logLevel}'. Supported levels: none, error, warning, info, debug, all, default.")
        };
    }

    internal static Dictionary<string, string>? BuildRequestHeaders(CodeServerSettings settings, CodeMcpTraceContextAccessor? accessor = null)
    {
        if (!settings.Copilot.ForwardTraceContext)
            return null;

        var context = CodeMcpTraceContext.Capture(accessor);
        var headers = context?.ToHeaders();
        return headers is { Count: > 0 } ? headers : null;
    }

    internal static IReadOnlyDictionary<string, string>? BuildClientEnvironment(CodeServerSettings settings)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddEssentialProcessEnvironment(env);

        if (settings.Copilot.ForwardTraceContext && CodeMcpTraceContext.Capture() is { } context)
        {
            foreach (var item in context.ToEnvironment())
                env[item.Key] = item.Value;
        }

        var telemetry = settings.Copilot.Telemetry;
        if (telemetry.Enabled)
        {
            AddEnv(env, "OTEL_SERVICE_NAME", telemetry.SourceName);
            AddEnv(env, "OTEL_EXPORTER_OTLP_ENDPOINT", ResolveTelemetryEndpoint(telemetry));
            AddEnv(env, "OTEL_TRACES_EXPORTER", string.IsNullOrWhiteSpace(telemetry.ExporterType) ? "otlp" : telemetry.ExporterType);
        }

        return env.Count == 0 ? null : env;
    }

    internal static TelemetryConfig? BuildTelemetryConfig(CodeCopilotTelemetrySettings telemetry)
    {
        if (!telemetry.Enabled)
            return null;

        return new TelemetryConfig
        {
            ExporterType = string.IsNullOrWhiteSpace(telemetry.ExporterType) ? "otlp" : telemetry.ExporterType,
            OtlpEndpoint = ResolveTelemetryEndpoint(telemetry),
            FilePath = string.IsNullOrWhiteSpace(telemetry.FilePath) ? null : telemetry.FilePath,
            SourceName = string.IsNullOrWhiteSpace(telemetry.SourceName) ? "GnOuGo.GithubCopilot.Mcp.Copilot" : telemetry.SourceName,
            CaptureContent = telemetry.CaptureContent
        };
    }

    private static IDisposable? StartCopilotActivity(CodeServerSettings settings, CodeMcpTraceContextAccessor accessor)
    {
        if (!settings.Copilot.ForwardTraceContext || Activity.Current is not null)
            return null;

        var context = accessor.Current ?? CodeMcpTraceContext.FromEnvironment();
        if (string.IsNullOrWhiteSpace(context?.TraceParent))
            return null;

        var activity = new Activity("GnOuGo.GithubCopilot.Mcp.Copilot.SuggestChange");
        activity.SetParentId(context.TraceParent);
        if (!string.IsNullOrWhiteSpace(context.TraceState))
            activity.TraceStateString = context.TraceState;
        activity.Start();
        return activity;
    }

    private static string? ResolveTelemetryEndpoint(CodeCopilotTelemetrySettings telemetry)
        => string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint)
            ? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            : telemetry.OtlpEndpoint;

    private static void AddEnv(Dictionary<string, string> env, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            env[name] = value;
    }

    private static void AddEssentialProcessEnvironment(Dictionary<string, string> env)
    {
        foreach (var name in GetEssentialProcessEnvironmentVariables())
            AddEnv(env, name, Environment.GetEnvironmentVariable(name));
    }

    private static IReadOnlyList<string> GetEssentialProcessEnvironmentVariables()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                "PATH",
                "PATHEXT",
                "SystemRoot",
                "WINDIR",
                "ComSpec",
                "TEMP",
                "TMP",
                "USERPROFILE",
                "HOME",
                "APPDATA",
                "LOCALAPPDATA",
                "PROGRAMDATA",
                "PROCESSOR_ARCHITECTURE",
                "PROCESSOR_ARCHITEW6432",
                "NUMBER_OF_PROCESSORS"
            ];
        }

        return
        [
            "PATH",
            "HOME",
            "TMPDIR",
            "TEMP",
            "TMP",
            "USER",
            "SHELL",
            "LANG",
            "LC_ALL"
        ];
    }

    private static string? BuildUsageJson(AssistantMessageData data)
    {
        if (data.OutputTokens is null && string.IsNullOrWhiteSpace(data.RequestId) && string.IsNullOrWhiteSpace(data.InteractionId))
            return null;

        return JsonSerializer.Serialize(new
        {
            data.OutputTokens,
            data.RequestId,
            data.InteractionId
        });
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildPrompt(string task, string projectRoot, IReadOnlyList<CodeFileContent> contextFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a coding assistant operating on a local project.");
        sb.AppendLine("Return a concise implementation plan and unified-diff style patches when changes are needed.");
        sb.AppendLine("Do not invent files that are not mentioned unless clearly necessary; explain assumptions.");
        sb.AppendLine();
        sb.AppendLine("[PROJECT ROOT]");
        sb.AppendLine(projectRoot);
        sb.AppendLine();
        sb.AppendLine("[TASK]");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("[CONTEXT FILES]");
        if (contextFiles.Count == 0)
        {
            sb.AppendLine("No file context was provided.");
        }
        else
        {
            foreach (var file in contextFiles)
            {
                sb.AppendLine($"--- {file.Path} ({file.LengthBytes} bytes) ---");
                sb.AppendLine(file.Content);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}



