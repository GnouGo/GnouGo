using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Abstraction for workflow telemetry / observability.
/// Follows OpenTelemetry GenAI semantic conventions without coupling to the OTel SDK.
/// https://opentelemetry.io/docs/specs/semconv/gen-ai/
///
/// The implementation (e.g., OTel-based) is injected by the host (CLI, Server, etc.)
/// so that GnOuGo.Flow.Core remains library-agnostic.
/// </summary>
public interface IWorkflowTelemetry
{
    /// <summary>
    /// Called when a workflow starts execution.
    /// Returns a context object that must be passed to <see cref="WorkflowEnd"/>.
    /// </summary>
    IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info);

    /// <summary>
    /// Called when a workflow finishes (success or failure).
    /// </summary>
    void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result);

    /// <summary>
    /// Called when a step starts execution.
    /// Returns a context object that must be passed to <see cref="StepEnd"/>.
    /// </summary>
    IStepSpan StepStart(IWorkflowSpan workflowSpan, StepTelemetryInfo info);

    /// <summary>
    /// Called when a step finishes execution (success, failure, or skipped).
    /// </summary>
    void StepEnd(IStepSpan span, StepResultInfo result);
}

/// <summary>
/// Opaque handle representing an in-flight workflow trace span.
/// </summary>
public interface IWorkflowSpan : IDisposable
{
}

/// <summary>
/// Opaque handle representing an in-flight step trace span.
/// Executors can call <see cref="SetAttribute"/> to attach GenAI semantic attributes.
/// </summary>
public interface IStepSpan : IDisposable
{
    /// <summary>
    /// Set a telemetry attribute on this span.
    /// Implementations map this to their tracing backend (e.g., Activity.SetTag for OTel).
    /// </summary>
    void SetAttribute(string key, object? value);

    /// <summary>
    /// Add a span event with attributes.
    /// Used for GenAI content events (gen_ai.content.prompt, gen_ai.content.completion, etc.).
    /// </summary>
    void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null);
}

// ────── Telemetry data models ──────

/// <summary>
/// Information about a workflow being started.
/// </summary>
public sealed class WorkflowTelemetryInfo
{
    /// <summary>Workflow name (e.g., "main").</summary>
    public string WorkflowName { get; init; } = "";

    /// <summary>Document name (if available).</summary>
    public string? DocumentName { get; init; }

    /// <summary>Input data provided to the workflow.</summary>
    public JsonNode? Inputs { get; init; }
}

/// <summary>
/// Information about a workflow that finished.
/// </summary>
public sealed class WorkflowResultInfo
{
    public bool Success { get; init; }
    public int StepsExecuted { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Information about a step being started.
/// </summary>
public sealed record StepTelemetryInfo
{
    /// <summary>Step identifier within the workflow.</summary>
    public string StepId { get; init; } = "";

    /// <summary>Step type (e.g., "llm.call", "mcp.call", "sequence").</summary>
    public string StepType { get; init; } = "";

    /// <summary>Resolved input for the step (may be null).</summary>
    public JsonNode? Input { get; init; }

    /// <summary>Current call depth (for sub-workflow calls).</summary>
    public int CallDepth { get; init; }

    // ── GenAI-specific attributes (populated for llm.call, mcp.call, etc.) ──

    /// <summary>gen_ai.operation.name — "chat", "embedding", "tool_call", "prompt_get", etc.</summary>
    public string? GenAiOperationName { get; init; }

    /// <summary>gen_ai.system — "openai", "anthropic", etc.</summary>
    public string? GenAiSystem { get; init; }

    /// <summary>gen_ai.request.model — Model name.</summary>
    public string? GenAiRequestModel { get; init; }

    /// <summary>gen_ai.request.temperature</summary>
    public double? GenAiRequestTemperature { get; init; }

    /// <summary>MCP server name (for mcp.call / mcp.list).</summary>
    public string? McpServerName { get; init; }

    /// <summary>MCP method name (for mcp.call single mode).</summary>
    public string? McpMethodName { get; init; }

    /// <summary>MCP kind: "tool" or "prompt".</summary>
    public string? McpKind { get; init; }
}

/// <summary>
/// Information about a step that finished.
/// </summary>
public sealed record StepResultInfo
{
    public StepStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public JsonNode? Output { get; init; }

    // ── GenAI-specific response attributes ──

    /// <summary>gen_ai.response.finish_reason — "stop", "tool_calls", "error", etc.</summary>
    public string? GenAiFinishReason { get; init; }

    /// <summary>gen_ai.usage.input_tokens</summary>
    public long? GenAiInputTokens { get; init; }

    /// <summary>gen_ai.usage.output_tokens</summary>
    public long? GenAiOutputTokens { get; init; }
}

// ────── Null (no-op) implementation ──────

/// <summary>
/// No-op telemetry implementation. Used when no telemetry provider is configured.
/// </summary>
public sealed class NullWorkflowTelemetry : IWorkflowTelemetry
{
    public static readonly NullWorkflowTelemetry Instance = new();

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => NullSpan.Instance;
    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
    public IStepSpan StepStart(IWorkflowSpan workflowSpan, StepTelemetryInfo info) => NullSpan.Instance;
    public void StepEnd(IStepSpan span, StepResultInfo result) { }

    private sealed class NullSpan : IWorkflowSpan, IStepSpan
    {
        public static readonly NullSpan Instance = new();
        public void SetAttribute(string key, object? value) { }
        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null) { }
        public void Dispose() { }
    }
}

