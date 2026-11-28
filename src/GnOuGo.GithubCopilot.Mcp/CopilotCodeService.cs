using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
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
    CodeProgressReporter reporter,
    BoundedCopilotTasks bounded,
    KeyVaultCopilotTaskStore? taskStore = null)
{
    internal async Task<CodeSuggestionResult> SuggestChangeAsync(string task, string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles, string? providerName, CancellationToken ct)
    {
        ValidateTask(task);
        using var activity = CopilotMcpConfiguration.StartCopilotActivity(options.Value, trace);
        var request = new CopilotSessionCreateRequest(configuration.Context(),
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
        IReadOnlyList<string> contextFiles, string? providerName, IReadOnlyList<string> expectedFiles,
        IReadOnlyList<string> commands, CancellationToken ct)
    {
        ValidateTask(task);
        var owner = configuration.Context();
        if (!options.Value.AllowWrites) throw new InvalidOperationException("Copilot agent edits are disabled by host policy.");
        if (expectedFiles.Count == 0 && contextFiles.Count == 0 && commands.Count == 0)
            throw new ArgumentException("Supply expectedFiles for verified edits, contextFiles for inspected files, or exact verificationCommands.");
        using var activity = CopilotMcpConfiguration.StartCopilotActivity(options.Value, trace, "AgentEdit");
        var limits = options.Value.Copilot.LogicalLimits;
        limits.Validate();
        var approval = new AgentTaskDefinition
        {
            Runner = "coding", Workspace = projectRoot, Objective = task + "\n" + BoundedWorkGuidance,
            Inputs = new() { ["requested_provider"] = providerName, ["context_files"] = new JsonArray(contextFiles.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) },
            Capabilities = ["project.read"],
            OutputSchema = JsonNode.Parse("""{"type":"object","required":["summary"],"properties":{"summary":{"type":"string"}},"additionalProperties":false}""")!.AsObject(),
            Budget = new() { MaxActions = limits.Interactions, MaxModelCalls = limits.InferenceAttempts,
                MaxTotalTokens = limits.ReservedTokens, MaxElapsedMilliseconds = limits.Seconds * 1000 }
        };
        if (expectedFiles.Count > 0) approval.Capabilities.Add("project.write");
        foreach (var path in expectedFiles)
            approval.Verification.Add(new("edit-" + approval.Verification.Count, "file.content", path,
                JsonNode.Parse("""{"type":"object","required":["exists","changed"],"properties":{"exists":{"const":true},"changed":{"const":true}}}""")!.AsObject()));
        foreach (var file in contextFiles.Where(path => !expectedFiles.Contains(path, StringComparer.Ordinal)))
            approval.Verification.Add(new("context-" + approval.Verification.Count, "file.content", file,
                JsonNode.Parse("""{"type":"object","required":["exists"],"properties":{"exists":{"const":true}}}""")!.AsObject()));
        if (commands.Count > 0) approval.Capabilities.Add("command.execute");
        foreach (var command in commands)
            approval.Verification.Add(new("check-" + approval.Verification.Count, "command.exit", command,
                JsonNode.Parse("""{"type":"object","required":["exit_code"],"properties":{"exit_code":{"type":"integer"}}}""")!.AsObject()));

        var identity = taskStore?.CurrentTaskId;
        var context = new AgentTaskContext(owner.TenantId, owner.RunId ?? identity ?? Guid.NewGuid().ToString("N"),
            owner.StepId ?? identity ?? "edit", approval);
        var result = await bounded.RunAsync(context, ct, providerName);
        if (result.Status == "needs_reconciliation" && identity is not null && taskStore is not null)
        {
            using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var error = JsonDocument.Parse("""{"code":-32603,"message":"COPILOT_NEEDS_RECONCILIATION: inspect the original bounded task before cleanup or retry."}""");
            await taskStore.SetFailedAsync(identity, error.RootElement, flush.Token);
        }
        var observed = await bounded.ReadExecutionObservationAsync(context, ct);
        var verified = result.Status == "completed" && (await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, ct)).All(v => v.Passed);
        var summary = result.Output?["summary"]?.ToString() ?? result.Message ?? result.Status;
        var events = (observed?.Events ?? []).Select(item =>
            new CodeProgressEvent(item.Kind, item.Level, item.Message, item.Timestamp)).ToArray();
        var traceContext = CodeMcpTraceContext.Capture(trace);
        return new(task, contextFiles, observed?.ModifiedFiles ?? [], summary, observed?.Model,
            Usage(observed?.Usage), events, observed is null ? summary : BuildAgentEditOutput(observed with { Content = summary, Completed = verified }, traceContext, events),
            traceContext?.TraceId, traceContext?.CorrelationId, traceContext?.TraceParent, verified,
            verified ? null : result.Failure?.Code ?? (result.Status == "needs_reconciliation" ? "COPILOT_NEEDS_RECONCILIATION" : "AGENT_VERIFICATION_FAILED"), verified ? null : result.Failure?.Message ?? summary)
        { ToolExecutions = observed?.ToolExecutions ?? [], CommandResults = CodeCommandResult.FromObservations(observed?.ToolExecutions ?? []) };
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

    internal const string BoundedWorkGuidance = "Read the applicable project instructions and the manifest entries needed for the next action. Run requested independent checks as separate terminal commands so their exit codes remain observable. Preserve host-provided package caches and offline settings. Read additional output ranges only for missing evidence, leaving room for the remaining actions and a complete response.";

    private static string BuildAgentEditOutput(
        CopilotSendResult result,
        CodeMcpTraceContext? traceContext,
        IReadOnlyList<CodeProgressEvent> progressEvents)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Completed ? "Copilot agent completed." : "Copilot response incomplete.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(result.Content))
        {
            sb.AppendLine("Summary:");
            sb.AppendLine(result.Content.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.Model))
            sb.AppendLine($"Model: {result.Model}");

        if (!string.IsNullOrWhiteSpace(traceContext?.TraceId))
            sb.AppendLine($"OpenTelemetry trace_id: {traceContext.TraceId}");
        if (!string.IsNullOrWhiteSpace(traceContext?.CorrelationId))
            sb.AppendLine($"Correlation ID: {traceContext.CorrelationId}");
        if (!string.IsNullOrWhiteSpace(result.Usage?.RequestId))
            sb.AppendLine($"Copilot request_id: {result.Usage.RequestId}");
        if (!string.IsNullOrWhiteSpace(result.Usage?.InteractionId))
            sb.AppendLine($"Copilot interaction_id: {result.Usage.InteractionId}");

        sb.AppendLine();
        var modifiedFiles = result.ModifiedFiles;
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
