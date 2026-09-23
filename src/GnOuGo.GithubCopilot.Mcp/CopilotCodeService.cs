using System.Text;
using System.Text.Json;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

/// <summary>Compatibility mapping for code_* tools; Core owns all execution.</summary>
internal sealed class CopilotCodeService(
    CopilotSessionManager sessions,
    CopilotMcpConfiguration configuration,
    CodePolicy policy,
    IOptions<CodeServerSettings> options,
    CodeMcpTraceContextAccessor trace,
    CodeProgressReporter reporter)
{
    internal async Task<CodeSuggestionResult> SuggestChangeAsync(string task, string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles, string? providerName, string? tenantId, CancellationToken ct)
    {
        ValidateTask(task);
        using var activity = CopilotMcpConfiguration.StartCopilotActivity(options.Value, trace);
        var request = new CopilotSessionCreateRequest(configuration.Context(tenantId),
            configuration.Build(projectRoot, providerName) with { AvailableTools = [], UseSessionFileSystem = true },
            CopilotSessionKind.OneShot, CopilotPermissionMode.Deny);
        var events = new List<CodeProgressEvent>();
        var progress = reporter.Capture();
        var result = await sessions.OneShotAsync(request, BuildPrompt(task, projectRoot, contextFiles), null, ct,
            value => Report(value, "code_suggest_change", events, progress), configuration.RequestHeaders());
        return new(task, contextFiles.Select(f => f.Path).ToArray(), result.Content, result.Model,
            Usage(result.Usage), events.ToArray());
    }

    internal async Task<CodeAgentEditResult> AgentEditAsync(string task, string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles, string? providerName, string? tenantId, CancellationToken ct)
    {
        ValidateTask(task);
        if (!options.Value.AllowWrites) throw new InvalidOperationException("Copilot agent edits are disabled by policy. Set Code:AllowWrites=true to enable code_agent_edit.");
        using var activity = CopilotMcpConfiguration.StartCopilotActivity(options.Value, trace, "AgentEdit");
        var request = new CopilotSessionCreateRequest(configuration.Context(tenantId), configuration.Build(projectRoot, providerName));
        var events = new List<CodeProgressEvent>();
        var progress = reporter.Capture();
        var result = await sessions.InteractiveOneShotAsync(request, BuildAgentEditPrompt(task, projectRoot, contextFiles), null, ct,
            value => Report(value, "code_agent_edit", events, progress), configuration.RequestHeaders());
        foreach (var file in result.ModifiedFiles)
            events.Add(reporter.Report("file_modified", "info", $"Modified {file}.", file, fallbackMethod: "code_agent_edit"));
        var context = CodeMcpTraceContext.Capture(trace);
        return new(task, contextFiles.Select(f => f.Path).ToArray(), result.ModifiedFiles, result.Content, result.Model,
            Usage(result.Usage), events.ToArray(), BuildAgentEditOutput(result.Content, result.ModifiedFiles, result.Model, context, events, result.Usage),
            context?.TraceId, context?.CorrelationId, context?.TraceParent) { ToolExecutions = result.ToolExecutions };
    }

    private void ValidateTask(string task)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("task must not be empty.", nameof(task));
        policy.EnsurePromptWithinLimit(task, nameof(task));
    }
    private static void Report(CopilotStreamEvent value, string method, List<CodeProgressEvent> events, CodeProgressReporter reporter)
    {
        var item = reporter.Report(value.Kind, value.Level, value.Message, fallbackServer: "GnOuGo.GithubCopilot.Mcp", fallbackMethod: method, fallbackMcpKind: "tool");
        lock (events) events.Add(item);
    }
    private static string? Usage(CopilotUsage? usage) => usage is null || (usage.OutputTokens is null && usage.RequestId is null && usage.InteractionId is null)
        ? null : JsonSerializer.Serialize(new CodeUsageInfo(usage.OutputTokens, usage.RequestId, usage.InteractionId), CodeMcpJsonContext.Default.CodeUsageInfo);
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

    private static string BuildAgentEditOutput(
        string summary,
        IReadOnlyList<string> modifiedFiles,
        string? model,
        CodeMcpTraceContext? traceContext,
        IReadOnlyList<CodeProgressEvent> progressEvents,
        CopilotUsage? data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Copilot agent completed.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine("Summary:");
            sb.AppendLine(summary.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(model))
            sb.AppendLine($"Model: {model}");

        if (!string.IsNullOrWhiteSpace(traceContext?.TraceId))
            sb.AppendLine($"OpenTelemetry trace_id: {traceContext.TraceId}");
        if (!string.IsNullOrWhiteSpace(traceContext?.CorrelationId))
            sb.AppendLine($"Correlation ID: {traceContext.CorrelationId}");
        if (!string.IsNullOrWhiteSpace(data?.RequestId))
            sb.AppendLine($"Copilot request_id: {data.RequestId}");
        if (!string.IsNullOrWhiteSpace(data?.InteractionId))
            sb.AppendLine($"Copilot interaction_id: {data.InteractionId}");

        sb.AppendLine();
        sb.AppendLine(modifiedFiles.Count == 0
            ? "Modified files: none reported."
            : $"Modified files ({modifiedFiles.Count}):");
        foreach (var file in modifiedFiles.Take(50))
            sb.AppendLine($"- {file}");
        if (modifiedFiles.Count > 50)
            sb.AppendLine($"- ... {modifiedFiles.Count - 50} more file(s)");

        var interestingEvents = progressEvents
            .Where(static e => e.Level is "warning" or "error" || e.Kind is "completed" or "file_modified" || e.Kind.StartsWith("sdk_", StringComparison.Ordinal))
            .TakeLast(25)
            .ToArray();

        if (interestingEvents.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Progress events:");
            foreach (var e in interestingEvents)
            {
                var fileSuffix = string.IsNullOrWhiteSpace(e.File) ? string.Empty : $" ({e.File})";
                sb.AppendLine($"- [{e.Level}] {e.Message}{fileSuffix}");
            }
        }

        return sb.ToString().Trim();
    }


}
