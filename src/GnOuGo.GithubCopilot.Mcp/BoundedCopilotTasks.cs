using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;

namespace GnOuGo.GithubCopilot.Mcp;

/// <summary>Durable task lifecycle around the single managed Copilot session implementation.</summary>
internal sealed class BoundedCopilotTasks(CopilotSessionManager sessions, CopilotMcpConfiguration configuration,
    ICopilotSessionFileSystemFactory fileSystems, IKeyVaultRecordStore records, CodeProgressReporter progress, CodePolicy policy)
{
    private const string Collection = "copilot-bounded-tasks-v9", Author = "GnOuGo.GithubCopilot.Mcp";
    private static readonly string[] Capabilities = ["project.read", "project.write", "command.execute"];
    internal string LockDirectory { get; init; } = GnOuGoWorkspace.ResolveDatabasePath(null, AppContext.BaseDirectory, ".GnOuGo/data/copilot-tasks-v9/owners");

    internal JsonObject Contract()
    {
        var schema = AgentTaskContracts.InputSchema;
        schema["properties"]!["capabilities"]!["items"]!["enum"] = new JsonArray(Capabilities.Where(c => policy.DescribePolicy().AllowWrites || c == "project.read").Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        schema["properties"]!["verification"]!["items"]!["properties"]!["kind"]!["enum"] = new JsonArray("command.exit", "file.content");
        return new()
        {
            ["schemaVersion"] = 9,
            ["contract"] = JsonSerializer.SerializeToNode(new AgentTaskRunnerContract(
                "Adaptive project work through managed Copilot. Capabilities: project.read, project.write, command.execute. " +
                "Project file tools obey the host file policy. Commands require a mandatory host sandbox, permit project writes and host-defined sandbox read-only locations, and deny network and credential grants. " +
                "command.exit verification subject is the exact shell command; facts: exit_code (integer), working_directory (string), tool_success (boolean). " +
                "file.content subject is a relative project file; facts: exists (boolean), sha256 (string), changed (boolean). " +
                "Require changed=true when an edit is required. Return structured JSON matching output_schema. Usage tokens are conservatively charged upper bounds.", schema), AgentTaskJsonContext.Default.AgentTaskRunnerContract)
        };
    }

    internal IReadOnlyList<string> Validate(AgentTaskContext context)
    {
        var errors = new List<string>();
        try
        {
            AgentTaskContracts.Parse(JsonSerializer.SerializeToNode(context.Task, AgentTaskJsonContext.Default.AgentTaskDefinition));
            ArgumentException.ThrowIfNullOrWhiteSpace(context.TenantId); ArgumentException.ThrowIfNullOrWhiteSpace(context.RunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.InvocationId);
            _ = configuration.Build(context.Task.Workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or WorkflowRuntimeException)
        { errors.Add("The task identity, workspace or contract is invalid under host policy."); }
        if (!policy.DescribePolicy().AllowWrites && context.Task.Capabilities.Any(c => c is "project.write" or "command.execute"))
            errors.Add("Host policy disables writes and adaptive command execution.");
        if (context.Task.Capabilities.Any(c => !Capabilities.Contains(c, StringComparer.Ordinal))) errors.Add("The task requests an unsupported capability.");
        if (context.Task.Capabilities.Contains("project.write") && !context.Task.Capabilities.Contains("project.read")) errors.Add("Project writes require project.read for verification.");
        foreach (var requirement in context.Task.Verification)
        {
            if (requirement.Kind is not ("command.exit" or "file.content")) errors.Add("The required evidence kind is unavailable.");
            if (requirement.Kind == "command.exit" && !context.Task.Capabilities.Contains("command.execute")) errors.Add("Command evidence requires command.execute.");
            if (requirement.Kind == "file.content" && (!context.Task.Capabilities.Contains("project.read") || Path.IsPathRooted(requirement.Subject) || requirement.Subject.Split('/', '\\').Contains("..")))
                errors.Add("File evidence requires a relative path within the approved project and project.read.");
        }
        return errors;
    }

    internal async Task<AgentTaskResult> InspectAsync(AgentTaskContext context, CancellationToken ct)
    {
        var record = await ReadAsync(context, ct);
        return record?["result"] is { } result
            ? JsonSerializer.Deserialize(result, AgentTaskJsonContext.Default.AgentTaskResult)!
            : Unknown(record, "No completed task receipt is available. The internal Copilot session is not automatically restored.");
    }

    internal async Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct)
    {
        Directory.CreateDirectory(LockDirectory);
        await using var owner = new FileStream(Path.Combine(LockDirectory, Hash(new JsonArray(context.TenantId, Key(context)).ToJsonString()) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (await ReadAsync(context, ct) is { } existing)
            return existing["result"] is { } prior ? JsonSerializer.Deserialize(prior, AgentTaskJsonContext.Default.AgentTaskResult)! : Unknown(existing, "This invocation was already dispatched; inspect or reconcile it without repeating its effects.");
        var validation = Validate(context);
        if (validation.Count > 0) return new("failed", null, [], [], new(0, 0, 0), string.Join(" ", validation));
        var started = DateTimeOffset.UtcNow;
        var record = new JsonObject
        {
            ["schemaVersion"] = 9, ["tenantId"] = context.TenantId, ["runId"] = context.RunId,
            ["invocationId"] = context.InvocationId, ["scopeHash"] = ScopeHash(context), ["status"] = "prepared",
            ["startedAt"] = started.ToString("O"), ["deadline"] = started.AddMilliseconds(context.Task.Budget.MaxElapsedMilliseconds).ToString("O"),
            ["modelCalls"] = 0, ["chargedTokens"] = 0L
        };
        await SaveAsync(context, record, ct);
        var tools = Tools(context.Task.Capabilities);
        var baseConfiguration = configuration.Build(context.Task.Workspace);
        var bounds = new CopilotExecutionBounds(context.Task.Budget.MaxModelCalls, context.Task.Budget.MaxTotalTokens,
            started.AddMilliseconds(context.Task.Budget.MaxElapsedMilliseconds), tools, async (calls, tokens, token) =>
            { record["modelCalls"] = calls; record["chargedTokens"] = tokens; await SaveAsync(context, record, token); }) {
                ApprovedPrompt = Prompt(context.Task),
                DeniedPaths = [Path.Combine(baseConfiguration.WorkingDirectory, GnOuGoWorkspace.WorkspaceDataSubfolder),
                    GnOuGoWorkspace.ResolveDatabasePath(null, AppContext.BaseDirectory, GnOuGoWorkspace.WorkspaceDataSubfolder)]
            };
        var runtime = baseConfiguration with
        {
            ExecutionBounds = bounds, EnableApproveAll = false, EnableSandboxBypassGrants = false,
            AvailableTools = tools.ToArray(), EnableConfigDiscovery = false, McpServers = null, SkillDirectories = [], DisabledSkills = ["*"],
            RequestTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(context.Task.Budget.MaxElapsedMilliseconds / 1000d)),
            ManagedSessionTtlSeconds = Math.Max(60, (int)Math.Ceiling(context.Task.Budget.MaxElapsedMilliseconds / 1000d) + 60)
        };
        var providerContext = configuration.Context(context.TenantId) with { RunId = context.RunId, StepId = context.InvocationId, ExecutionId = context.ExecutionId ?? context.RunId, AgentId = context.AgentId, AgentName = context.AgentName };
        var create = new CopilotSessionCreateRequest(providerContext, runtime, CopilotSessionKind.Managed, CopilotPermissionMode.Interactive, Streaming: true);
        string? handle = null;
        var dispatched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(context.Task.Budget.MaxElapsedMilliseconds));
        AgentTaskResult result;
        try
        {
            await using var files = fileSystems.Create(create);
            var before = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var requirement in context.Task.Verification.Where(v => v.Kind == "file.content"))
                before[requirement.Subject] = await FileHashAsync(files, requirement.Subject, deadline.Token);
            record["before"] = new JsonObject(before.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
            var descriptor = await sessions.CreateAsync(create, deadline.Token);
            handle = descriptor.Handle; record["handle"] = handle; record["status"] = "dispatched";
            await SaveAsync(context, record, deadline.Token);
            dispatched = true;
            var reporter = progress.Capture();
            var sent = await sessions.SendAsync(new(providerContext, handle, Prompt(context.Task), AgentMode: "interactive")
            {
                RequestHeaders = configuration.RequestHeaders(),
                Progress = e => reporter.Report(e.Kind, e.Level, e.Message, fallbackServer: Author, fallbackMethod: "copilot_task_run", fallbackMcpKind: "tool")
            }, deadline.Token);
            var evidence = CommandEvidence(sent, runtime.WorkingDirectory, context.Task.Verification);
            var uncertain = !sent.Completed || sent.ToolExecutions.Any(t => !t.CompletionObserved || t.ConflictingCompletion) ||
                sent.ToolExecutions.Where(t => t.ToolName is "bash" or "powershell").Any(t => !CommandCompleted(t, sent.ToolExecutions));
            if (uncertain) result = Unknown(null, "The observed tool events do not establish that all dispatched work has stopped.");
            else
            {
                // Close the managed session before collecting final file evidence. This is the existing lifecycle API.
                await sessions.DeleteAsync(providerContext, handle, CancellationToken.None); handle = null;
                var artifacts = new List<AgentTaskArtifact>();
                foreach (var requirement in context.Task.Verification.Where(v => v.Kind == "file.content"))
                {
                    var hash = await FileHashAsync(files, requirement.Subject, deadline.Token);
                    evidence.Add(new("file:" + requirement.Id, "file.content", requirement.Subject,
                        new() { ["exists"] = hash is not null, ["sha256"] = hash ?? "", ["changed"] = before[requirement.Subject] != hash }));
                    if (hash is not null) artifacts.Add(new(requirement.Id, "file", Path.Combine(runtime.WorkingDirectory, requirement.Subject), hash));
                }
                JsonNode? output = null;
                try { output = JsonNode.Parse(sent.Content); } catch (JsonException) { }
                result = new(bounds.Exhausted ? "budget_exhausted" : "completed", output, evidence, artifacts, new(0, 0, 0));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = dispatched ? Unknown(null, bounds.Exhausted ? "The approved inference budget is exhausted; reconcile interrupted work." : "Execution was interrupted; inspect and reconcile the original invocation.")
                : new("failed", null, [], [], new(0, 0, 0), ex is CopilotSandboxRequiredException ? ex.Message : "The host rejected task preparation before dispatch (" + ex.GetType().Name + ").");
        }
        finally
        {
            await bounds.StopAsync();
            if (handle is not null)
            {
                // Abort is best effort and is not proof of quiescence. Keep uncertainty in the receipt.
                try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await sessions.AbortAsync(providerContext, handle, stop.Token); } catch (Exception) { }
            }
        }
        result = result with { Usage = Usage(record, started) };
        record["status"] = result.Status; record["result"] = JsonSerializer.SerializeToNode(result, AgentTaskJsonContext.Default.AgentTaskResult);
        await SaveAsync(context, record, CancellationToken.None);
        return result;
    }

    private async Task<JsonObject?> ReadAsync(AgentTaskContext context, CancellationToken ct)
    {
        var saved = await records.GetAsync(Collection, context.TenantId, Key(context), Author, ct);
        if (saved is null) return null;
        var record = JsonNode.Parse(saved.Value)!.AsObject();
        if (record["schemaVersion"]?.GetValue<int>() != 9 || record["tenantId"]?.GetValue<string>() != context.TenantId ||
            record["runId"]?.GetValue<string>() != context.RunId || record["invocationId"]?.GetValue<string>() != context.InvocationId || record["scopeHash"]?.GetValue<string>() != ScopeHash(context))
            throw new InvalidOperationException("The stored task scope or ownership differs. Regenerate and approve a new task.");
        return record;
    }
    private async Task SaveAsync(AgentTaskContext context, JsonObject record, CancellationToken ct)
        => _ = await records.UpsertAsync(Collection, context.TenantId, Key(context), record.ToJsonString(), Author, ct);
    private static string Key(AgentTaskContext context) => Hash(new JsonArray(context.RunId, context.InvocationId).ToJsonString());
    private static string ScopeHash(AgentTaskContext context) => Hash(JsonSerializer.Serialize(context.Task, AgentTaskJsonContext.Default.AgentTaskDefinition));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static AgentTaskUsage Usage(JsonObject? record, DateTimeOffset? started = null) => new(
        record?["modelCalls"]?.GetValue<int>() ?? 0, record?["chargedTokens"]?.GetValue<long>() ?? 0,
        started is null ? 0 : (DateTimeOffset.UtcNow - started.Value).TotalMilliseconds) { Metering = "reserved_upper_bound" };
    private static AgentTaskResult Unknown(JsonObject? record, string message) => new("needs_reconciliation", null, [], [], Usage(record), message);
    private static async Task<string?> FileHashAsync(ICopilotSessionFileSystem files, string path, CancellationToken ct)
    { try { return Hash(await files.ReadFileAsync(path, ct)); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } }
    private static HashSet<string> Tools(IReadOnlyList<string> capabilities)
    {
        var tools = new HashSet<string>(StringComparer.Ordinal);
        if (capabilities.Contains("project.read")) tools.UnionWith(["project_read", "project_list", "project_stat"]);
        if (capabilities.Contains("project.write")) tools.UnionWith(["project_write", "project_append", "project_mkdir", "project_remove", "project_rename"]);
        if (capabilities.Contains("command.execute")) tools.UnionWith(OperatingSystem.IsWindows() ? ["powershell", "read_powershell", "write_powershell", "stop_powershell"] : new[] { "bash", "read_bash", "write_bash", "stop_bash" });
        return tools;
    }
    private static string Prompt(AgentTaskDefinition task) =>
        "Complete only the approved objective below, within its declared capabilities and workspace. " +
        "Run each required verification command exactly as written and wait for its structured exit status. " +
        "Do not leave background work running. Do not request scope expansion. Return only JSON matching output_schema; completion claims do not establish evidence.\n" +
        JsonSerializer.Serialize(task, AgentTaskJsonContext.Default.AgentTaskDefinition);

    private static bool CommandCompleted(CopilotToolExecutionObservation tool, IReadOnlyList<CopilotToolExecutionObservation> all)
        => tool.CompletionObserved && !tool.ConflictingCompletion && tool.Terminals.Count > 0 && tool.Terminals.All(t => Terminal(tool, t, all) is not null);
    private static CopilotTerminalObservation? Terminal(CopilotToolExecutionObservation tool, CopilotTerminalObservation terminal, IReadOnlyList<CopilotToolExecutionObservation> all)
        => terminal.ExitCode is not null ? terminal : terminal.ShellId is null ? null :
            all.Where(t => t.CompletionObserved && !t.ConflictingCompletion && t.ToolSucceeded == true)
                .SelectMany(t => t.Terminals).LastOrDefault(t => t.ShellId == terminal.ShellId && t.ExitCode is not null);
    private static List<AgentTaskEvidence> CommandEvidence(CopilotSendResult sent, string workingDirectory, IReadOnlyList<AgentVerificationRequirement> requirements)
    {
        var evidence = new List<AgentTaskEvidence>();
        var commands = new Dictionary<string, List<(CopilotToolExecutionObservation Tool, CopilotTerminalObservation Terminal)>>(StringComparer.Ordinal);
        var required = requirements.Where(r => r.Kind == "command.exit").Select(r => r.Subject).ToHashSet(StringComparer.Ordinal);
        long lastMutation = 0;
        foreach (var tool in sent.ToolExecutions)
        {
            if (tool.ToolName is "project_write" or "project_append" or "project_mkdir" or "project_remove" or "project_rename")
                lastMutation = Math.Max(lastMutation, tool.CompletedSequence ?? long.MaxValue);
            if (tool.ToolName is not ("bash" or "powershell")) continue;
            JsonNode? args;
            try { args = JsonNode.Parse(tool.ArgumentsJson ?? "null"); } catch (JsonException) { lastMutation = long.MaxValue; continue; }
            if (args?["command"] is not JsonValue command || !command.TryGetValue<string>(out var text) || !required.Contains(text))
            { lastMutation = Math.Max(lastMutation, tool.CompletedSequence ?? long.MaxValue); continue; }
            if (!CommandCompleted(tool, sent.ToolExecutions)) continue;
            if (!commands.TryGetValue(text, out var attempts)) commands[text] = attempts = [];
            attempts.AddRange(tool.Terminals.Select(t => (tool, Terminal(tool, t, sent.ToolExecutions)!)));
        }
        foreach (var (command, attempts) in commands)
        {
            var ordered = attempts.OrderBy(a => a.Tool.CompletedSequence).ToArray();
            var lastCall = ordered[^1].Tool.ToolCallId;
            var latest = ordered.LastOrDefault(a => a.Tool.ToolCallId == lastCall && a.Terminal.ExitCode != 0);
            if (latest.Tool is null) latest = ordered[^1];
            // A test from before a later edit or unclassified command cannot verify the final task state.
            if (latest.Tool.StartedSequence is not { } started || latest.Tool.CompletedSequence is null || started <= lastMutation) continue;
            var history = new JsonArray(ordered.Select(a => (JsonNode)new JsonObject
            { ["tool_call_id"] = a.Tool.ToolCallId, ["exit_code"] = a.Terminal.ExitCode, ["tool_success"] = a.Tool.ToolSucceeded == true,
              ["started_sequence"] = a.Tool.StartedSequence, ["completed_sequence"] = a.Tool.CompletedSequence }).ToArray());
            // Earlier failed attempts remain in the facts; the final observed attempt determines the verification outcome.
            evidence.Add(new(latest.Tool.ToolCallId, "command.exit", command,
                new() { ["exit_code"] = latest.Terminal.ExitCode, ["working_directory"] = latest.Terminal.WorkingDirectory ?? workingDirectory,
                    ["tool_success"] = latest.Tool.ToolSucceeded == true, ["attempts"] = history }));
        }
        return evidence;
    }
}
