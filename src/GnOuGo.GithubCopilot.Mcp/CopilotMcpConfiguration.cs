using System.Diagnostics;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CopilotMcpConfiguration(CodePolicy policy, IOptions<CodeServerSettings> options, CodeMcpTraceContextAccessor trace)
{
    private readonly CodeServerSettings _settings = options.Value;
    internal CopilotRuntimeConfiguration Build(string? projectRoot, string? provider = null, string? model = null)
    {
        var root = string.IsNullOrWhiteSpace(projectRoot) ? policy.DefaultWorkingDirectory : policy.ResolveProjectRoot(projectRoot);
        var providerName = string.IsNullOrWhiteSpace(provider)
            ? (string.Equals(_settings.Copilot.Provider, "Copilot", StringComparison.OrdinalIgnoreCase) ? null : _settings.Copilot.Provider) : provider.Trim();
        return BuildRuntimeConfiguration(_settings, root, policy.ResolveConfiguredToken()) with
        {
            ProviderName = providerName,
            Model = string.IsNullOrWhiteSpace(model) ? _settings.Copilot.Model : model.Trim()
        };
    }
    internal CopilotRequestContext Context(string? tenantId)
    {
        var context = trace.Current ?? CodeMcpTraceContext.Capture(trace);
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? context?.TenantId : tenantId.Trim();
        if (string.IsNullOrWhiteSpace(tenant)) throw new McpException("TenantId is required in _meta.gnougo.tenantId or the tenantId argument.");
        return new(tenant, context?.CorrelationId, context?.RunId, context?.StepId, context?.Repository,
            context?.PullRequestNumber, context?.HeadSha, context?.ExecutionId, context?.AgentId, context?.AgentName);
    }
    internal IReadOnlyDictionary<string, string>? RequestHeaders() => BuildRequestHeaders(_settings, trace);
    internal static CopilotRuntimeConfiguration BuildRuntimeConfiguration(CodeServerSettings settings, string projectRoot, string? token)
        => new(projectRoot, settings.Copilot.Model, settings.Copilot.ReasoningEffort,
            GitHubToken: token, UseLoggedInUser: settings.Copilot.UseLoggedInUser,
            RequestTimeoutSeconds: settings.Copilot.RequestTimeoutSeconds,
            ManagedSessionTtlSeconds: settings.Copilot.ManagedSessionTtlSeconds,
            EnableApproveAll: settings.Copilot.EnableApproveAll,
            Environment: BuildClientEnvironment(settings))
        {
            UseSessionFileSystem = true,
            EnableSandboxBypassGrants = settings.Copilot.EnableSandboxBypassGrants,
            LogLevel = settings.Copilot.LogLevel,
            Telemetry = BuildTelemetryConfig(settings.Copilot.Telemetry)
        };
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

    internal static CopilotTelemetryConfiguration? BuildTelemetryConfig(CodeCopilotTelemetrySettings telemetry)
        => !telemetry.Enabled ? null : new(
            string.IsNullOrWhiteSpace(telemetry.ExporterType) ? "otlp" : telemetry.ExporterType,
            ResolveTelemetryEndpoint(telemetry), telemetry.FilePath,
            string.IsNullOrWhiteSpace(telemetry.SourceName) ? "GnOuGo.GithubCopilot.Mcp.Copilot" : telemetry.SourceName,
            telemetry.CaptureContent);

    internal static IDisposable? StartCopilotActivity(CodeServerSettings settings, CodeMcpTraceContextAccessor accessor, string operation = "SuggestChange")
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

}
