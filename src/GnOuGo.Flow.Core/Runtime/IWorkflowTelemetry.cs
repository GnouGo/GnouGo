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
    /// Called when a workflow starts under an existing workflow or step span.
    /// Implementations that can preserve trace hierarchy should use <paramref name="parentSpan"/>.
    /// </summary>
    IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info) => WorkflowStart(info);

    /// <summary>
    /// Called when a workflow finishes (success or failure).
    /// </summary>
    void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result);

    /// <summary>
    /// Called when a step starts execution.
    /// Returns a context object that must be passed to <see cref="StepEnd"/>.
    /// </summary>
    IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info);

    /// <summary>
    /// Called when a step finishes execution (success, failure, or skipped).
    /// </summary>
    void StepEnd(IStepSpan span, StepResultInfo result);

    /// <summary>
    /// Called when an internal phase starts inside an existing workflow/step span.
    /// These spans are not counted as workflow steps; they are used for real-time
    /// observability of long-running operations such as workflow planning phases.
    /// </summary>
    ITelemetrySpan SpanStart(ITelemetrySpan parentSpan, TelemetrySpanInfo info) => NullTelemetrySpan.Instance;

    /// <summary>
    /// Called when an internal phase span finishes.
    /// </summary>
    void SpanEnd(ITelemetrySpan span, TelemetrySpanResultInfo result) { }
}

/// <summary>
/// Opaque handle representing an in-flight telemetry span.
/// </summary>
public interface ITelemetrySpan : IDisposable
{
    /// <summary>
    /// Set a telemetry attribute on this span.
    /// Implementations map this to their tracing backend (e.g., Activity.SetTag for OTel).
    /// </summary>
    void SetAttribute(string key, object? value) { }

    /// <summary>
    /// Add a span event with attributes.
    /// Used for GenAI content events and phase diagnostics.
    /// </summary>
    void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null) { }
}

/// <summary>
/// Opaque handle representing an in-flight workflow trace span.
/// </summary>
public interface IWorkflowSpan : ITelemetrySpan
{
}

/// <summary>
/// Opaque handle representing an in-flight step trace span.
/// Executors can call <see cref="ITelemetrySpan.SetAttribute"/> to attach GenAI semantic attributes.
/// </summary>
public interface IStepSpan : ITelemetrySpan
{
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

    /// <summary>Original workflow source text when available, usually YAML.</summary>
    public string? SourceText { get; init; }

    /// <summary>Workflow source format, for example <c>yaml</c> or <c>json</c>.</summary>
    public string? SourceFormat { get; init; }
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

/// <summary>
/// Information about an internal child span started inside a workflow or step span.
/// </summary>
public sealed record TelemetrySpanInfo
{
    public string Name { get; init; } = "";
    public string? Phase { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public int? CallDepth { get; init; }
    public IReadOnlyList<KeyValuePair<string, object?>>? Attributes { get; init; }
}

/// <summary>
/// Information about an internal child span that finished.
/// </summary>
public sealed record TelemetrySpanResultInfo
{
    public bool Success { get; init; } = true;
    public TimeSpan Duration { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Shared no-op telemetry span.
/// </summary>
public sealed class NullTelemetrySpan : IWorkflowSpan, IStepSpan
{
    public static readonly NullTelemetrySpan Instance = new();
    private NullTelemetrySpan() { }
    public void Dispose() { }
}

// ────── Null (no-op) implementation ──────

/// <summary>
/// No-op telemetry implementation. Used when no telemetry provider is configured.
/// </summary>
public sealed class NullWorkflowTelemetry : IWorkflowTelemetry
{
    public static readonly NullWorkflowTelemetry Instance = new();

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => NullTelemetrySpan.Instance;
    public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info) => NullTelemetrySpan.Instance;
    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
    public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info) => NullTelemetrySpan.Instance;
    public void StepEnd(IStepSpan span, StepResultInfo result) { }
}
