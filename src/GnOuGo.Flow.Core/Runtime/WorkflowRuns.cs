using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Recovery is an executor contract, not an inference from a tool's name.</summary>
public enum StepRecovery { External, ReplaySafe, Composite, HumanInput }

public static class WorkflowRunStatus
{
    public const string Running = "running", Completed = "completed", Failed = "failed",
        Cancelled = "cancelled", WaitingForHuman = "waiting_for_human", NeedsReconciliation = "needs_reconciliation";
}

/// <summary>The authoritative schema-9 execution journal. Hosts must encrypt the complete payload.</summary>
public sealed class WorkflowRun
{
    public int SchemaVersion { get; set; } = 9;
    public string TenantId { get; set; } = "";
    public string RunId { get; set; } = "";
    public long Revision { get; set; }
    public string WorkflowName { get; set; } = "";
    public string WorkflowYaml { get; set; } = "";
    public string DefinitionHash { get; set; } = "";
    public JsonNode? Inputs { get; set; }
    public ExecutionLimits Limits { get; set; } = new();
    public LLMUsageBudgetLimits? ModelBudget { get; set; }
    public LLMUsageBudgetSnapshot? ModelUsage { get; set; }
    public int StepsStarted { get; set; }
    public int FinalizationStepsStarted { get; set; }
    public string Status { get; set; } = WorkflowRunStatus.Running;
    public bool CancelRequested { get; set; }
    public bool FinalizationStarted { get; set; }
    public bool FinalizationCompleted { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, WorkflowInvocation> Invocations { get; set; } = new(StringComparer.Ordinal);
    public List<WorkflowRunEvent> Events { get; set; } = [];
    public RunResult? Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowInvocation
{
    public string Id { get; set; } = "";
    public string StepType { get; set; } = "";
    public StepRecovery Recovery { get; set; }
    public bool IsFinalization { get; set; }
    public string Status { get; set; } = "prepared";
    public JsonNode? ResolvedInput { get; set; }
    public JsonObject DataBefore { get; set; } = new();
    public JsonObject? DataAfter { get; set; }
    public JsonNode? Output { get; set; }
    public JsonNode? Observation { get; set; }
    public bool ExternalCompletionObserved { get; set; }
    public WorkflowError? Error { get; set; }
    public Dictionary<string, JsonNode?> Control { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset PreparedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record WorkflowRunCommand(long ExpectedRevision, string? InvocationId = null, string? ConfirmedStoppedReason = null, JsonNode? Response = null);

public sealed record WorkflowRunEvent(DateTimeOffset Timestamp, string Kind, string? InvocationId);
public sealed class WorkflowRunConflictException(string message) : InvalidOperationException(message);

/// <summary>Tenant-scoped records with a single execution owner and revision-checked commands.</summary>
public interface IWorkflowRunStore
{
    Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default);
    Task CreateAsync(WorkflowRun run, CancellationToken ct = default);
    Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default);
    Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default);
    Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId, JsonNode? response, CancellationToken ct = default);
}

public interface IWorkflowRunLease : IAsyncDisposable
{
    WorkflowRun Run { get; }
    /// <summary>Atomically persists a new revision; merges cancellation requested while the owner was active.</summary>
    Task SaveAsync(CancellationToken ct = default);
    Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default);
}

public static class WorkflowRunStorage
{
    public const string IncompatibleMessage = "This execution uses an incompatible storage schema. Regenerate and approve the workflow; the original encrypted record remains unchanged.";

    public static WorkflowRun Read(string json, string tenantId, string runId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 9)
            throw new WorkflowRunConflictException(IncompatibleMessage);
        if (root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new WorkflowRunConflictException("The execution journal contains ambiguous fields.");
        var run = JsonSerializer.Deserialize(json, WorkflowRunJsonContext.Default.WorkflowRun)
            ?? throw new WorkflowRunConflictException("The execution journal is invalid.");
        ValidateOwnership(run, tenantId, runId);
        return run;
    }

    public static void ValidateOwnership(WorkflowRun run, string tenantId, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (run.SchemaVersion != 9) throw new WorkflowRunConflictException(IncompatibleMessage);
        if (run.TenantId != tenantId || run.RunId != runId || run.Limits.TenantId != tenantId || run.Limits.RunId != runId)
            throw new WorkflowRunConflictException("Execution journal ownership is invalid.");
    }

    public static WorkflowRun Clone(WorkflowRun run) => Read(JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun), run.TenantId, run.RunId);

    public static void Answer(WorkflowRun run, string invocationId, JsonNode? response)
    {
        if (run.CancelRequested || !run.Invocations.TryGetValue(invocationId, out var invocation) ||
            invocation.Recovery != StepRecovery.HumanInput || invocation.Status != "waiting_for_human" || invocation.Control.ContainsKey("human_response"))
            throw new WorkflowRunConflictException("This invocation is no longer waiting for an answer.");
        if (invocation.Control.GetValueOrDefault("human_deadline") is { } deadline && deadline.GetValue<long>() < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new WorkflowRunConflictException("The original human-input deadline has expired. Resume the run to record its timeout.");
        invocation.Control["human_response"] = response?.DeepClone();
        run.Events.Add(new(DateTimeOffset.UtcNow, "human_answer", invocationId));
    }

    /// <summary>The execution owner merges only commands that are allowed while it holds the run.</summary>
    public static void MergeCommands(WorkflowRun target, WorkflowRun saved)
    {
        target.CancelRequested |= saved.CancelRequested;
        foreach (var invocation in saved.Invocations.Values)
            if (invocation.Control.TryGetValue("human_response", out var response) && target.Invocations.TryGetValue(invocation.Id, out var current))
                current.Control["human_response"] = response?.DeepClone();
        target.Events.AddRange(saved.Events.Where(e => e.Kind is "cancel_requested" or "human_answer" && !target.Events.Contains(e)));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkflowRun))]
[JsonSerializable(typeof(List<WorkflowRun>))]
[JsonSerializable(typeof(WorkflowRunCommand))]
[JsonSerializable(typeof(ExecutionLimits))]
[JsonSerializable(typeof(WorkflowDocument))]
public partial class WorkflowRunJsonContext : JsonSerializerContext;
