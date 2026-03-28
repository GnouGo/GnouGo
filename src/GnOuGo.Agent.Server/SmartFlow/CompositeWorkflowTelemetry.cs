using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Delegates every telemetry call to two inner implementations simultaneously:
/// <list type="bullet">
///   <item><see cref="AgentStreamingTelemetry"/> — forwards thinking/chunk events to the Blazor UI</item>
///   <item><see cref="AgentOTelTelemetry"/>       — emits OpenTelemetry traces and metrics</item>
/// </list>
/// </summary>
public sealed class CompositeWorkflowTelemetry : IWorkflowTelemetry
{
    private readonly IWorkflowTelemetry _streaming;
    private readonly IWorkflowTelemetry _otel;

    public CompositeWorkflowTelemetry(IWorkflowTelemetry streaming, IWorkflowTelemetry otel)
    {
        _streaming = streaming;
        _otel      = otel;
    }

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
    {
        var s = _streaming.WorkflowStart(info);
        var o = _otel.WorkflowStart(info);
        return new CompositeSpan(s, o);
    }

    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
    {
        if (span is not CompositeSpan cs) return;
        _streaming.WorkflowEnd(cs.Streaming, result);
        _otel.WorkflowEnd(cs.OTel, result);
    }

    public IStepSpan StepStart(IWorkflowSpan workflowSpan, StepTelemetryInfo info)
    {
        var cs = workflowSpan as CompositeSpan;
        var s = _streaming.StepStart(cs?.Streaming ?? workflowSpan, info);
        var o = _otel.StepStart(cs?.OTel ?? workflowSpan, info);
        return new CompositeStepSpan(s, o);
    }

    public void StepEnd(IStepSpan span, StepResultInfo result)
    {
        if (span is not CompositeStepSpan cs) return;
        _streaming.StepEnd(cs.Streaming, result);
        _otel.StepEnd(cs.OTel, result);
    }

    // ── Span wrappers ────────────────────────────────────────────────────────

    private sealed class CompositeSpan(IWorkflowSpan streaming, IWorkflowSpan otel) : IWorkflowSpan
    {
        public IWorkflowSpan Streaming { get; } = streaming;
        public IWorkflowSpan OTel      { get; } = otel;
        public void Dispose() { Streaming.Dispose(); OTel.Dispose(); }
    }

    private sealed class CompositeStepSpan(IStepSpan streaming, IStepSpan otel) : IStepSpan
    {
        public IStepSpan Streaming { get; } = streaming;
        public IStepSpan OTel      { get; } = otel;

        public void SetAttribute(string key, object? value)
        {
            Streaming.SetAttribute(key, value);
            OTel.SetAttribute(key, value);
        }

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            Streaming.AddEvent(name, attributes);
            OTel.AddEvent(name, attributes);
        }

        public void Dispose() { Streaming.Dispose(); OTel.Dispose(); }
    }
}

