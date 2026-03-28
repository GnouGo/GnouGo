using System.Diagnostics;
using System.Diagnostics.Metrics;
using GnOuGo.AI.Core.Telemetry;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// OpenTelemetry-based <see cref="IWorkflowTelemetry"/> for GnOuGo.Agent.Server.
/// Emits traces and metrics following GenAI semantic conventions.
/// Uses the same ActivitySource/Meter names as GnOuGo.Flow.Server so spans appear
/// grouped in the OTLP collector.
/// </summary>
public sealed class AgentOTelTelemetry : IWorkflowTelemetry, IDisposable
{
    public const string ActivitySourceName = "GnOuGo.Flow.Workflow";
    public const string MeterName         = "GnOuGo.Flow.Workflow";

    private readonly ActivitySource _source;
    private readonly Meter _meter;
    private readonly Counter<long>      _stepCounter;
    private readonly Histogram<double>  _stepDuration;
    private readonly Counter<long>      _tokenUsage;
    private readonly Histogram<double>  _workflowDuration;

    public AgentOTelTelemetry()
    {
        _source = new ActivitySource(ActivitySourceName, "1.0.0");
        _meter  = new Meter(MeterName, "1.0.0");

        _stepCounter      = _meter.CreateCounter<long>("gnougo-flow.step.count",
            description: "Number of workflow steps executed");
        _stepDuration     = _meter.CreateHistogram<double>("gnougo-flow.step.duration",
            unit: "s", description: "Duration of individual workflow steps");
        _tokenUsage       = _meter.CreateCounter<long>("gen_ai.client.token.usage",
            unit: "{token}", description: "Tokens used in GenAI operations");
        _workflowDuration = _meter.CreateHistogram<double>("gnougo-flow.workflow.duration",
            unit: "s", description: "Duration of workflow executions");
    }

    // ── IWorkflowTelemetry ───────────────────────────────────────────────────

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
    {
        var a = _source.StartActivity("workflow", ActivityKind.Internal);
        if (a is null) return new WfSpan(null);
        a.SetTag("gnougo-flow.workflow.name", info.WorkflowName);
        if (info.DocumentName is not null) a.SetTag("gnougo-flow.document.name", info.DocumentName);
        return new WfSpan(a);
    }

    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
    {
        if (span is not WfSpan ws || ws.Activity is null) return;
        ws.Activity.SetTag("gnougo-flow.workflow.steps_executed", result.StepsExecuted);
        if (result.Success)
            ws.Activity.SetStatus(ActivityStatusCode.Ok);
        else
        {
            ws.Activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
            ws.Activity.SetTag("error.type",    result.ErrorCode);
            ws.Activity.SetTag("error.message", result.ErrorMessage);
        }
        _workflowDuration.Record(result.Duration.TotalSeconds,
            new KeyValuePair<string, object?>("gnougo-flow.workflow.name",    ws.Activity.GetTagItem("gnougo-flow.workflow.name")),
            new KeyValuePair<string, object?>("gnougo-flow.workflow.success", result.Success));
    }

    public IStepSpan StepStart(IWorkflowSpan workflowSpan, StepTelemetryInfo info)
    {
        var parent = (workflowSpan as WfSpan)?.Activity;
        var a = parent is not null
            ? _source.StartActivity(SpanName(info), ActivityKind.Client,
                new ActivityContext(parent.TraceId, parent.SpanId, ActivityTraceFlags.Recorded))
            : _source.StartActivity(SpanName(info), ActivityKind.Client);

        if (a is null) return new StSpan(null);
        a.SetTag("gnougo-flow.step.id",   info.StepId);
        a.SetTag("gnougo-flow.step.type", info.StepType);
        a.SetTag("gnougo-flow.step.call_depth", info.CallDepth);
        if (info.GenAiOperationName  is not null) a.SetTag("gen_ai.operation.name",    info.GenAiOperationName);
        if (info.GenAiSystem         is not null) a.SetTag("gen_ai.system",             info.GenAiSystem);
        if (info.GenAiRequestModel   is not null) a.SetTag("gen_ai.request.model",      info.GenAiRequestModel);
        if (info.GenAiRequestTemperature.HasValue) a.SetTag("gen_ai.request.temperature", info.GenAiRequestTemperature.Value);
        if (info.McpServerName       is not null) a.SetTag("mcp.server.name",           info.McpServerName);
        if (info.McpMethodName       is not null) a.SetTag("mcp.method.name",           info.McpMethodName);
        if (info.McpKind             is not null) a.SetTag("mcp.kind",                  info.McpKind);
        return new StSpan(a);
    }

    public void StepEnd(IStepSpan span, StepResultInfo result)
    {
        if (span is not StSpan ss || ss.Activity is null) return;
        var stepType = ss.Activity.GetTagItem("gnougo-flow.step.type") as string ?? "unknown";
        var stepId   = ss.Activity.GetTagItem("gnougo-flow.step.id")   as string ?? "unknown";

        switch (result.Status)
        {
            case StepStatus.Succeeded:
                ss.Activity.SetStatus(ActivityStatusCode.Ok); break;
            case StepStatus.Skipped:
                ss.Activity.SetTag("gnougo-flow.step.skipped", true);
                ss.Activity.SetStatus(ActivityStatusCode.Ok); break;
            case StepStatus.Failed:
                ss.Activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
                ss.Activity.SetTag("error.type",    result.ErrorCode);
                ss.Activity.SetTag("error.message", result.ErrorMessage); break;
        }

        if (result.GenAiFinishReason   is not null) ss.Activity.SetTag("gen_ai.response.finish_reason", result.GenAiFinishReason);
        if (result.GenAiInputTokens.HasValue)        ss.Activity.SetTag("gen_ai.usage.input_tokens",   result.GenAiInputTokens.Value);
        if (result.GenAiOutputTokens.HasValue)       ss.Activity.SetTag("gen_ai.usage.output_tokens",  result.GenAiOutputTokens.Value);

        var model = ss.Activity.GetTagItem("gen_ai.request.model") as string;
        if (model is not null && (result.GenAiInputTokens.HasValue || result.GenAiOutputTokens.HasValue))
        {
            var cost = ModelPricingCatalog.EstimateCost(model,
                result.GenAiInputTokens ?? 0, result.GenAiOutputTokens ?? 0);
            if (cost.HasValue)
                ss.Activity.SetTag("gen_ai.usage.cost", (double)cost.Value);
        }

        _stepCounter.Add(1,
            new KeyValuePair<string, object?>("gnougo-flow.step.type",   stepType),
            new KeyValuePair<string, object?>("gnougo-flow.step.status", result.Status.ToString()));
        _stepDuration.Record(result.Duration.TotalSeconds,
            new KeyValuePair<string, object?>("gnougo-flow.step.type", stepType),
            new KeyValuePair<string, object?>("gnougo-flow.step.id",   stepId));

        if (result.GenAiInputTokens.HasValue)
            _tokenUsage.Add(result.GenAiInputTokens.Value,
                new KeyValuePair<string, object?>("gen_ai.token.type",        "input"),
                new KeyValuePair<string, object?>("gen_ai.request.model",     model));
        if (result.GenAiOutputTokens.HasValue)
            _tokenUsage.Add(result.GenAiOutputTokens.Value,
                new KeyValuePair<string, object?>("gen_ai.token.type",        "output"),
                new KeyValuePair<string, object?>("gen_ai.request.model",     model));
    }

    public void Dispose()
    {
        _source.Dispose();
        _meter.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SpanName(StepTelemetryInfo info)
    {
        if (info.GenAiOperationName is not null && info.GenAiRequestModel is not null)
            return $"{info.GenAiOperationName} {info.GenAiRequestModel}";
        if (info.GenAiOperationName is not null)
            return info.GenAiOperationName;
        return $"{info.StepType} {info.StepId}";
    }

    // ── Span wrappers ────────────────────────────────────────────────────────

    private sealed class WfSpan(Activity? activity) : IWorkflowSpan
    {
        public Activity? Activity { get; } = activity;
        public void Dispose() => Activity?.Dispose();
    }

    private sealed class StSpan(Activity? activity) : IStepSpan
    {
        public Activity? Activity { get; } = activity;

        public void SetAttribute(string key, object? value) => Activity?.SetTag(key, value);

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            if (Activity is null) return;
            if (attributes is not null)
            {
                var tags = new ActivityTagsCollection();
                foreach (var kv in attributes) tags[kv.Key] = kv.Value;
                Activity.AddEvent(new ActivityEvent(name, tags: tags));
            }
            else
            {
                Activity.AddEvent(new ActivityEvent(name));
            }
        }

        public void Dispose() => Activity?.Dispose();
    }
}

