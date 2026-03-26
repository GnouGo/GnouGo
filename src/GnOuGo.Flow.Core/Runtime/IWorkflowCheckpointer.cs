using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Describes the persisted state of a workflow execution at a checkpoint.
/// </summary>
public sealed class WorkflowCheckpoint
{
    /// <summary>Unique run identifier.</summary>
    public string RunId { get; set; } = "";

    /// <summary>Workflow name (for display / logging).</summary>
    public string WorkflowName { get; set; } = "";

    /// <summary>Index of the next step to execute (0-based).</summary>
    public int NextStepIndex { get; set; }

    /// <summary>
    /// Accumulated step outputs so far (keyed by step id).
    /// Allows the engine to resume from where it left off.
    /// </summary>
    public JsonObject StepOutputs { get; set; } = new();

    /// <summary>Original inputs passed to the workflow.</summary>
    public JsonNode? Inputs { get; set; }

    /// <summary>The compiled workflow definition (serialised).</summary>
    public string WorkflowYaml { get; set; } = "";

    /// <summary>Status: running, paused, completed, failed.</summary>
    public string Status { get; set; } = "running";

    /// <summary>UTC timestamp of the checkpoint.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional tenant id for multi-tenant isolation.</summary>
    public string? TenantId { get; set; }
}

/// <summary>
/// Abstraction for persisting and restoring workflow checkpoints.
/// Implementations: SqliteWorkflowCheckpointer, InMemoryWorkflowCheckpointer.
/// </summary>
public interface IWorkflowCheckpointer
{
    /// <summary>Save or update a checkpoint for a running workflow.</summary>
    Task SaveAsync(WorkflowCheckpoint checkpoint, CancellationToken ct);

    /// <summary>Load the most recent checkpoint for a run.</summary>
    Task<WorkflowCheckpoint?> LoadAsync(string runId, CancellationToken ct);

    /// <summary>Delete a checkpoint (after successful completion).</summary>
    Task DeleteAsync(string runId, CancellationToken ct);

    /// <summary>List all checkpoints (optionally filtered by status or tenant).</summary>
    Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(string? tenantId = null, string? status = null, CancellationToken ct = default);
}

