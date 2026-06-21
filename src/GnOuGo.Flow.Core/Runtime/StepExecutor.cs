using System.Text.Json.Nodes;
using System.Diagnostics;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

public sealed record StepExceptionDoc(string Code, bool Retryable, string Description);
public sealed record StepExceptionCatalog(string StepType, IReadOnlyList<StepExceptionDoc> Exceptions);

/// <summary>
/// Interface for step executors. One per step type.
/// </summary>
public interface IStepExecutor
{
    string StepType { get; }
    Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Declarative input/output contract used by compile-time and workflow.plan validation.
    /// Custom executors should override this property; built-in executors are resolved from
    /// <see cref="BuiltInStepContracts"/> by default.
    /// </summary>
    StepContract? Contract => BuiltInStepContracts.Get(StepType);

    /// <summary>
    /// Returns a short DSL reference snippet for this step type.
    /// Used by workflow.plan to dynamically build comprehensive LLM prompts.
    /// Each executor owns its own documentation, keeping it maintainable.
    /// Return null to exclude from generated prompts.
    /// </summary>
    string? DslSnippet => null;

    /// <summary>
    /// Returns documented runtime exceptions for this step type.
    /// Used by workflow.plan to explain retry/on_error strategy with real error codes.
    /// Return null to omit task-specific exception documentation.
    /// </summary>
    IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => null;
}

/// <summary>
/// Context for step execution.
/// </summary>
public sealed class StepExecutionContext
{
    public CompiledStep Step { get; init; } = null!;
    public JsonObject Data { get; init; } = null!;
    public WorkflowEngine Engine { get; init; } = null!;
    public ExecutionLimits Limits { get; init; } = new();
    public int CallDepth { get; init; }
    public HashSet<string> CallStack { get; init; } = new();

    /// <summary>
    /// The active telemetry span for this step.
    /// Executors use <see cref="SetTelemetryAttribute"/> to attach data
    /// (model name, tokens, server name, etc.) directly from the source.
    /// </summary>
    internal IStepSpan? TelemetrySpan { get; set; }

    /// <summary>
    /// Collected telemetry attributes written by the executor.
    /// Read by the WorkflowEngine when building <see cref="StepResultInfo"/>.
    /// </summary>
    internal Dictionary<string, object?> TelemetryAttributes { get; } = new();

    /// <summary>
    /// Set a telemetry attribute on the current step span.
    /// Uses OpenTelemetry GenAI semantic convention keys, e.g.:
    /// "gen_ai.request.model", "gen_ai.usage.input_tokens", "gen_ai.response.finish_reason",
    /// "mcp.server.name", etc.
    ///
    /// Can be called at any point during step execution.
    /// The attribute is written both to the active span (for real-time tracing)
    /// and to the collected dictionary (for StepResultInfo at step end).
    /// </summary>
    public void SetTelemetryAttribute(string key, object? value)
    {
        TelemetryAttributes[key] = value;
        TelemetrySpan?.SetAttribute(key, value);
    }

    /// <summary>
    /// Add a telemetry span event on the current step span.
    /// Used for standard GenAI content events such as
    /// "gen_ai.content.prompt" and "gen_ai.content.completion".
    /// </summary>
    public void AddTelemetryEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
    {
        TelemetrySpan?.AddEvent(name, attributes);
    }

    /// <summary>
    /// Starts an internal child span under the current step span. Child spans are intended for
    /// long-running executor phases and do not count as workflow steps.
    /// </summary>
    public TelemetrySpanScope BeginTelemetrySpan(
        string name,
        string? phase = null,
        IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
    {
        var parent = TelemetrySpan ?? NullTelemetrySpan.Instance;
        return BeginTelemetrySpan(parent, name, phase, attributes);
    }

    /// <summary>
    /// Starts an internal child span under an explicit parent span. This is useful when an
    /// executor has nested phases that should be grouped under one logical operation span.
    /// </summary>
    public TelemetrySpanScope BeginTelemetrySpan(
        ITelemetrySpan parent,
        string name,
        string? phase = null,
        IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
    {
        var allAttributes = new List<KeyValuePair<string, object?>>
        {
            new("gnougo-flow.step.id", Step.Id),
            new("gnougo-flow.step.type", Step.Type),
            new("gnougo-flow.step.call_depth", CallDepth)
        };
        if (!string.IsNullOrWhiteSpace(phase))
            allAttributes.Add(new("gnougo-flow.plan.phase", phase));
        if (attributes != null)
            allAttributes.AddRange(attributes);

        var span = Engine.Telemetry.SpanStart(parent, new TelemetrySpanInfo
        {
            Name = name,
            Phase = phase,
            StepId = Step.Id,
            StepType = Step.Type,
            CallDepth = CallDepth,
            Attributes = allAttributes
        });
        return new TelemetrySpanScope(Engine.Telemetry, span);
    }
}

/// <summary>
/// Disposable scope for internal telemetry child spans.
/// </summary>
public sealed class TelemetrySpanScope : IDisposable
{
    private readonly IWorkflowTelemetry _telemetry;
    private readonly ITelemetrySpan _span;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private bool _ended;
    private bool _success = true;
    private string? _errorType;
    private string? _errorMessage;

    internal TelemetrySpanScope(IWorkflowTelemetry telemetry, ITelemetrySpan span)
    {
        _telemetry = telemetry;
        _span = span;
    }

    public void SetAttribute(string key, object? value) => _span.SetAttribute(key, value);

    public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        => _span.AddEvent(name, attributes);

    internal ITelemetrySpan Span => _span;

    public void Fail(Exception ex)
    {
        _success = false;
        _errorType = ex.GetType().Name;
        _errorMessage = ex.Message;
    }

    public void Fail(string errorType, string? errorMessage = null)
    {
        _success = false;
        _errorType = errorType;
        _errorMessage = errorMessage;
    }

    public void Complete() => _success = true;

    public void Dispose()
    {
        if (_ended)
            return;
        _ended = true;
        _stopwatch.Stop();
        _telemetry.SpanEnd(_span, new TelemetrySpanResultInfo
        {
            Success = _success,
            Duration = _stopwatch.Elapsed,
            ErrorType = _errorType,
            ErrorMessage = _errorMessage
        });
        _span.Dispose();
    }
}

/// <summary>
/// Registry of step executors.
/// </summary>
public sealed class StepExecutorRegistry
{
    private readonly Dictionary<string, IStepExecutor> _executors = new();

    public void Register(IStepExecutor executor) => _executors[executor.StepType] = executor;

    public IStepExecutor? Get(string stepType) =>
        _executors.TryGetValue(stepType, out var e) ? e : null;

    public bool Has(string stepType) => _executors.ContainsKey(stepType);

    public IEnumerable<string> RegisteredTypes => _executors.Keys;

    public IReadOnlyDictionary<string, StepContract> GetContracts()
    {
        var contracts = new Dictionary<string, StepContract>(StringComparer.Ordinal);
        foreach (var executor in _executors.Values)
        {
            if (executor.Contract != null)
                contracts[executor.StepType] = executor.Contract;
        }

        return contracts;
    }

    /// <summary>
    /// Collects DSL snippets from all registered executors.
    /// Optionally filtered to only include specific step types.
    /// </summary>
    public IEnumerable<string> GetDslSnippets(HashSet<string>? allowedTypes = null)
    {
        foreach (var executor in _executors.Values)
        {
            if (allowedTypes != null && !allowedTypes.Contains(executor.StepType))
                continue;
            var snippet = executor.DslSnippet;
            if (snippet != null)
                yield return snippet;
        }
    }

    public IEnumerable<StepExceptionCatalog> GetStepExceptionCatalogs(HashSet<string>? allowedTypes = null)
    {
        foreach (var executor in _executors.Values)
        {
            if (allowedTypes != null && !allowedTypes.Contains(executor.StepType))
                continue;
            var exceptions = executor.DocumentedExceptions;
            if (exceptions != null && exceptions.Count > 0)
                yield return new StepExceptionCatalog(executor.StepType, exceptions);
        }
    }
}
