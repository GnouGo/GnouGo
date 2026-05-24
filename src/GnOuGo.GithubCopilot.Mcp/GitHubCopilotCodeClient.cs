using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class GitHubCopilotCodeClient : ICodeAssistantClient
{
    private readonly CodeServerSettings _settings;
    private readonly CodePolicy _policy;
    private readonly ICopilotProviderConfigResolver _providerConfigResolver;
    private readonly CodeMcpTraceContextAccessor _traceContextAccessor;
    private readonly CodeProgressReporter _progressReporter;
    private readonly ILogger<GitHubCopilotCodeClient> _logger;

    public GitHubCopilotCodeClient(
        IOptions<CodeServerSettings> settings,
        CodePolicy policy,
        ICopilotProviderConfigResolver providerConfigResolver,
        CodeMcpTraceContextAccessor traceContextAccessor,
        CodeProgressReporter progressReporter,
        ILogger<GitHubCopilotCodeClient> logger)
    {
        _settings = settings.Value;
        _policy = policy;
        _providerConfigResolver = providerConfigResolver;
        _traceContextAccessor = traceContextAccessor;
        _progressReporter = progressReporter;
        _logger = logger;
    }

    public async Task<CodeSuggestionResult> SuggestChangeAsync(
        string task,
        string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles,
        string? providerName,
        CancellationToken cancellationToken)
    {
        var progress = new CodeProgressRecorder(_progressReporter, "code_suggest_change");
        progress.Add("prepare", "thinking", "Preparing Copilot code suggestion request.");

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
        progress.Add("provider", "thinking", "Resolving Copilot provider and model.");
        var providerOverride = await _providerConfigResolver.ResolveAsync(
            providerName,
            _settings.Copilot.Model,
            token,
            cancellationToken);
        var model = providerOverride?.Model ?? _settings.Copilot.Model;

        await using var client = CreateClient(projectRoot, token);
        progress.Add("client_start", "thinking", "Starting Copilot SDK client.");
        await client.StartAsync(cancellationToken);

        progress.Add("session_create", "thinking", "Creating Copilot suggestion session.");
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
        using var sdkProgressSubscription = session.On<SessionEvent>(progress.AddSdkEvent);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.Copilot.RequestTimeoutSeconds));
        progress.Add("request_send", "thinking", "Sending code suggestion request to Copilot.");
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
        progress.Add("completed", "info", "Copilot returned a code suggestion.");

        return new CodeSuggestionResult(
            Task: task,
            Files: contextFiles.Select(static file => file.Path).ToArray(),
            Suggestion: suggestion,
            Model: model,
            UsageJson: data is null ? null : BuildUsageJson(data),
            ProgressEvents: progress.ToArray());
    }

    public async Task<CodeAgentEditResult> AgentEditAsync(
        string task,
        string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles,
        string? providerName,
        CancellationToken cancellationToken)
    {
        var progress = new CodeProgressRecorder(_progressReporter, "code_agent_edit");
        progress.Add("prepare", "thinking", "Preparing Copilot agent edit request.");

        if (string.IsNullOrWhiteSpace(task))
            throw new InvalidOperationException("task must not be empty.");
        if (!_settings.AllowWrites)
            throw new InvalidOperationException("Copilot agent file edits are disabled by policy. Set Code:AllowWrites=true to enable code_agent_edit.");
        _policy.EnsurePromptWithinLimit(task, nameof(task));

        var prompt = BuildAgentEditPrompt(task, projectRoot, contextFiles);
        var token = _policy.ResolveConfiguredToken();
        if (!_settings.Copilot.UseLoggedInUser && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "A GitHub token is required unless Code:Copilot:UseLoggedInUser=true. " +
                "For Copilot SDK authentication, use a locally signed-in GitHub account or configure Code:Copilot:ApiKey / token environment variables.");
        }

        using var activity = StartCopilotActivity(_settings, _traceContextAccessor, "AgentEdit");
        progress.Add("provider", "thinking", "Resolving Copilot provider and model.");
        var providerOverride = await _providerConfigResolver.ResolveAsync(
            providerName,
            _settings.Copilot.Model,
            token,
            cancellationToken);
        var model = providerOverride?.Model ?? _settings.Copilot.Model;
        LocalProjectSessionFsProvider? sessionFsProvider = null;

        await using var client = CreateClient(projectRoot, token, enableSessionFs: true);
        progress.Add("client_start", "thinking", "Starting Copilot SDK client with controlled session filesystem.");
        await client.StartAsync(cancellationToken);

        progress.Add("session_create", "thinking", "Creating Copilot agent session.");
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            ClientName = "GnOuGo.GithubCopilot.Mcp",
            Model = model,
            ReasoningEffort = NormalizeNullable(_settings.Copilot.ReasoningEffort),
            Provider = providerOverride?.Provider,
            WorkingDirectory = projectRoot,
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            CreateSessionFsProvider = _ =>
            {
                sessionFsProvider = new LocalProjectSessionFsProvider(_policy, _settings, projectRoot, _logger);
                return sessionFsProvider;
            }
        }, cancellationToken);
        using var sdkProgressSubscription = session.On<SessionEvent>(progress.AddSdkEvent);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.Copilot.RequestTimeoutSeconds));
        progress.Add("request_send", "thinking", "Sending agent edit request to Copilot.");
        var response = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = prompt,
            Mode = "agent",
            RequestHeaders = BuildRequestHeaders(_settings, _traceContextAccessor)
        }, timeout, cancellationToken);

        var data = response?.Data;
        var summary = data?.Content;
        if (string.IsNullOrWhiteSpace(summary))
            summary = "GitHub Copilot agent completed without a textual summary.";

        var modifiedFiles = sessionFsProvider?.ModifiedFiles ?? [];
        if (modifiedFiles.Count == 0)
        {
            progress.Add("completed", "info", "Copilot agent completed without reporting modified files.");
        }
        else
        {
            progress.Add("completed", "info", $"Copilot agent modified {modifiedFiles.Count} file(s).");
            foreach (var file in modifiedFiles.Take(20))
                progress.Add("file_modified", "info", $"Modified {file}.", file);
        }
        _logger.LogInformation(
            "GitHub Copilot SDK agent edit completed using provider {Provider}, model {Model}, modifiedFiles={ModifiedFileCount}.",
            providerOverride?.ProviderName ?? _settings.Copilot.Provider,
            model,
            modifiedFiles.Count);

        return new CodeAgentEditResult(
            Task: task,
            ContextFiles: contextFiles.Select(static file => file.Path).ToArray(),
            ModifiedFiles: modifiedFiles,
            Summary: summary,
            Model: model,
            UsageJson: data is null ? null : BuildUsageJson(data),
            ProgressEvents: progress.ToArray());
    }

    internal CopilotClient CreateClient(string projectRoot, string? token, bool enableSessionFs = false)
        => new(BuildClientOptions(_settings, projectRoot, token, _logger, enableSessionFs));

    internal static CopilotClientOptions BuildClientOptions(
        CodeServerSettings settings,
        string projectRoot,
        string? token,
        ILogger? logger = null,
        bool enableSessionFs = false)
    {
        if (!settings.Copilot.UseLoggedInUser && string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A GitHub token is required when Code:Copilot:UseLoggedInUser=false.", nameof(token));

        var options = new CopilotClientOptions
        {
            WorkingDirectory = projectRoot,
            GitHubToken = string.IsNullOrWhiteSpace(token) ? null : token,
            UseLoggedInUser = settings.Copilot.UseLoggedInUser,
            LogLevel = NormalizeLogLevel(settings.Copilot.LogLevel),
            Environment = BuildClientEnvironment(settings),
            Telemetry = BuildTelemetryConfig(settings.Copilot.Telemetry),
            Logger = logger
        };

        if (enableSessionFs)
        {
            options.SessionFs = new SessionFsConfig
            {
                InitialWorkingDirectory = projectRoot,
                SessionStatePath = ".gnougo/copilot-session-state",
                Conventions = OperatingSystem.IsWindows()
                    ? SessionFsSetProviderConventions.Windows
                    : SessionFsSetProviderConventions.Posix
            };
        }

        return options;
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

    internal static CopilotLogLevel? NormalizeLogLevel(string? logLevel)
    {
        if (string.IsNullOrWhiteSpace(logLevel))
            return CopilotLogLevel.Warning;

        return logLevel.Trim().ToLowerInvariant() switch
        {
            "warn" => CopilotLogLevel.Warning,
            "trace" => CopilotLogLevel.All,
            "none" => CopilotLogLevel.None,
            "error" => CopilotLogLevel.Error,
            "warning" => CopilotLogLevel.Warning,
            "info" => CopilotLogLevel.Info,
            "debug" => CopilotLogLevel.Debug,
            "all" => CopilotLogLevel.All,
            "default" => null,
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

    private static IDisposable? StartCopilotActivity(CodeServerSettings settings, CodeMcpTraceContextAccessor accessor, string operation = "SuggestChange")
    {
        if (!settings.Copilot.ForwardTraceContext || Activity.Current is not null)
            return null;

        var context = accessor.Current ?? CodeMcpTraceContext.FromEnvironment();
        if (string.IsNullOrWhiteSpace(context?.TraceParent))
            return null;

        var activity = new Activity($"GnOuGo.GithubCopilot.Mcp.Copilot.{operation}");
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

        var usage = new CodeUsageInfo(data.OutputTokens, data.RequestId, data.InteractionId);
        return JsonSerializer.Serialize(usage, CodeMcpJsonContext.Default.CodeUsageInfo);
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

    private static string BuildAgentEditPrompt(string task, string projectRoot, IReadOnlyList<CodeFileContent> contextFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a coding agent operating on a local project through a controlled session filesystem.");
        sb.AppendLine("Implement the requested change by editing files directly when necessary.");
        sb.AppendLine("Only create, update, rename, or delete files required by the task.");
        sb.AppendLine("Respect the project structure and avoid unrelated formatting changes.");
        sb.AppendLine("When finished, return a concise summary and list the files changed.");
        sb.AppendLine();
        sb.AppendLine("[PROJECT ROOT]");
        sb.AppendLine(projectRoot);
        sb.AppendLine();
        sb.AppendLine("[TASK]");
        sb.AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("[INITIAL CONTEXT FILES]");
        if (contextFiles.Count == 0)
        {
            sb.AppendLine("No file context was provided. Inspect the project as needed before editing.");
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

    private sealed class CodeProgressRecorder
    {
        private readonly CodeProgressReporter _reporter;
        private readonly string _mcpMethod;
        private readonly CopilotSdkProgressEventMapper _sdkProgressEventMapper = new();
        private readonly object _gate = new();
        private readonly List<CodeProgressEvent> _events = new();

        public CodeProgressRecorder(CodeProgressReporter reporter, string mcpMethod)
        {
            _reporter = reporter;
            _mcpMethod = mcpMethod;
        }

        public void Add(string kind, string level, string message, string? file = null)
        {
            var progressEvent = _reporter.Report(
                kind,
                level,
                message,
                file,
                fallbackServer: "GnOuGo.GithubCopilot.Mcp",
                fallbackMethod: _mcpMethod,
                fallbackMcpKind: "tool");
            lock (_gate)
                _events.Add(progressEvent);
        }

        public void AddSdkEvent(SessionEvent sdkEvent)
        {
            if (!_sdkProgressEventMapper.TryMap(sdkEvent, out var progressEvent))
                return;

            var reportedEvent = _reporter.Report(
                progressEvent,
                fallbackServer: "GnOuGo.GithubCopilot.Mcp",
                fallbackMethod: _mcpMethod,
                fallbackMcpKind: "tool");
            lock (_gate)
                _events.Add(reportedEvent);
        }

        public IReadOnlyList<CodeProgressEvent> ToArray()
        {
            lock (_gate)
                return _events.ToArray();
        }
    }
}



