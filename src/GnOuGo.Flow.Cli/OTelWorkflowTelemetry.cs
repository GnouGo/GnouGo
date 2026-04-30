using System.Diagnostics;
using System.Diagnostics.Metrics;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

/// <summary>
/// OpenTelemetry-based implementation of IWorkflowTelemetry.
/// Uses System.Diagnostics.ActivitySource (OTel-compatible) and Meter for traces &amp; metrics.
///
/// Follows the OpenTelemetry GenAI semantic conventions:
/// https://opentelemetry.io/docs/specs/semconv/gen-ai/
///
/// To export traces/metrics, configure the OTel SDK with:
///   .AddSource(OTelWorkflowTelemetry.ActivitySourceName)
///   .AddMeter(OTelWorkflowTelemetry.MeterName)
/// </summary>
public sealed class OTelWorkflowTelemetry : IWorkflowTelemetry, IDisposable
{
    public const string ActivitySourceName = "GnOuGo.Flow.Workflow";
    public const string MeterName = "GnOuGo.Flow.Workflow";

    private readonly ActivitySource _source;
    private readonly Meter _meter;

    // Metrics
    private readonly Counter<long> _stepCounter;
    private readonly Histogram<double> _stepDurationHistogram;
    private readonly Counter<long> _tokenUsageCounter;
    private readonly Histogram<double> _workflowDurationHistogram;

    public OTelWorkflowTelemetry()
    {
        _source = new ActivitySource(ActivitySourceName, "1.0.0");
        _meter = new Meter(MeterName, "1.0.0");

        _stepCounter = _meter.CreateCounter<long>(
            "gnougo-flow.step.count",
            description: "Number of workflow steps executed");

        _stepDurationHistogram = _meter.CreateHistogram<double>(
            "gnougo-flow.step.duration",
            unit: "s",
            description: "Duration of individual workflow steps");

        _tokenUsageCounter = _meter.CreateCounter<long>(
            "gen_ai.client.token.usage",
            unit: "{token}",
            description: "Number of tokens used in GenAI operations");

        _workflowDurationHistogram = _meter.CreateHistogram<double>(
            "gnougo-flow.workflow.duration",
            unit: "s",
            description: "Duration of workflow executions");
    }

    // -- Workflow span --

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
    {
        var activity = _source.StartActivity("workflow", ActivityKind.Internal);
        if (activity == null)
            return new OTelWorkflowSpan(null);

        activity.SetTag("gnougo-flow.workflow.name", info.WorkflowName);
        if (info.DocumentName != null)
            activity.SetTag("gnougo-flow.document.name", info.DocumentName);
        ApplyWorkflowSourceTags(activity, info);

        return new OTelWorkflowSpan(activity);
    }

    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
    {
        if (span is not OTelWorkflowSpan ws || ws.Activity == null)
            return;

        ws.Activity.SetTag("gnougo-flow.workflow.success", result.Success);
        ws.Activity.SetTag("gnougo-flow.workflow.steps_executed", result.StepsExecuted);

        if (!result.Success)
        {
            ws.Activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
            ws.Activity.SetTag("error.type", result.ErrorCode);
            ws.Activity.SetTag("error.message", result.ErrorMessage);
        }
        else
        {
            ws.Activity.SetStatus(ActivityStatusCode.Ok);
        }

        _workflowDurationHistogram.Record(result.Duration.TotalSeconds,
            new KeyValuePair<string, object?>("gnougo-flow.workflow.name",
                ws.Activity.GetTagItem("gnougo-flow.workflow.name")),
            new KeyValuePair<string, object?>("gnougo-flow.workflow.success", result.Success));
    }

    // -- Step span --

    public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
    {
        var parentActivity = ResolveParentActivity(parentSpan);

        // Start step as child of workflow
        Activity? activity;
        if (parentActivity != null)
        {
            var parentContext = new ActivityContext(parentActivity.TraceId, parentActivity.SpanId, ActivityTraceFlags.Recorded);
            activity = _source.StartActivity(BuildStepSpanName(info), ActivityKind.Client, parentContext);
        }
        else
        {
            activity = _source.StartActivity(BuildStepSpanName(info), ActivityKind.Client);
        }

        if (activity == null)
            return new OTelStepSpan(null);

        // Standard step attributes (identity)
        activity.SetTag("gnougo-flow.step.id", info.StepId);
        activity.SetTag("gnougo-flow.step.type", info.StepType);
        activity.SetTag("gnougo-flow.step.call_depth", info.CallDepth);

        // GenAI/MCP attrs from StepTelemetryInfo (if pre-populated)
        if (info.GenAiOperationName != null)
            activity.SetTag("gen_ai.operation.name", info.GenAiOperationName);
        if (info.GenAiSystem != null)
            activity.SetTag("gen_ai.system", info.GenAiSystem);
        if (info.GenAiRequestModel != null)
            activity.SetTag("gen_ai.request.model", info.GenAiRequestModel);
        if (info.GenAiRequestTemperature.HasValue)
            activity.SetTag("gen_ai.request.temperature", info.GenAiRequestTemperature.Value);
        if (info.McpServerName != null)
            activity.SetTag("mcp.server.name", info.McpServerName);
        if (info.McpMethodName != null)
            activity.SetTag("mcp.method.name", info.McpMethodName);
        if (info.McpKind != null)
            activity.SetTag("mcp.kind", info.McpKind);

        return new OTelStepSpan(activity);
    }

    public void StepEnd(IStepSpan span, StepResultInfo result)
    {
        if (span is not OTelStepSpan ss || ss.Activity == null)
            return;

        var stepType = ss.Activity.GetTagItem("gnougo-flow.step.type") as string ?? "unknown";
        var stepId = ss.Activity.GetTagItem("gnougo-flow.step.id") as string ?? "unknown";

        // Status
        switch (result.Status)
        {
            case StepStatus.Succeeded:
                ss.Activity.SetStatus(ActivityStatusCode.Ok);
                break;
            case StepStatus.Skipped:
                ss.Activity.SetTag("gnougo-flow.step.skipped", true);
                ss.Activity.SetStatus(ActivityStatusCode.Ok);
                break;
            case StepStatus.Failed:
                ss.Activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
                ss.Activity.SetTag("error.type", result.ErrorCode);
                ss.Activity.SetTag("error.message", result.ErrorMessage);
                break;
        }

        // GenAI response attributes
        if (result.GenAiFinishReason != null)
            ss.Activity.SetTag("gen_ai.response.finish_reason", result.GenAiFinishReason);
        if (result.GenAiInputTokens.HasValue)
            ss.Activity.SetTag("gen_ai.usage.input_tokens", result.GenAiInputTokens.Value);
        if (result.GenAiOutputTokens.HasValue)
            ss.Activity.SetTag("gen_ai.usage.output_tokens", result.GenAiOutputTokens.Value);

        // Metrics
        _stepCounter.Add(1,
            new KeyValuePair<string, object?>("gnougo-flow.step.type", stepType),
            new KeyValuePair<string, object?>("gnougo-flow.step.status", result.Status.ToString()));

        _stepDurationHistogram.Record(result.Duration.TotalSeconds,
            new KeyValuePair<string, object?>("gnougo-flow.step.type", stepType),
            new KeyValuePair<string, object?>("gnougo-flow.step.id", stepId));

        // Token usage metrics (GenAI convention)
        if (result.GenAiInputTokens.HasValue)
        {
            _tokenUsageCounter.Add(result.GenAiInputTokens.Value,
                new KeyValuePair<string, object?>("gen_ai.token.type", "input"),
                new KeyValuePair<string, object?>("gen_ai.request.model",
                    ss.Activity.GetTagItem("gen_ai.request.model")));
        }
        if (result.GenAiOutputTokens.HasValue)
        {
            _tokenUsageCounter.Add(result.GenAiOutputTokens.Value,
                new KeyValuePair<string, object?>("gen_ai.token.type", "output"),
                new KeyValuePair<string, object?>("gen_ai.request.model",
                    ss.Activity.GetTagItem("gen_ai.request.model")));
        }
    }

    // -- Helpers --

    private static string BuildStepSpanName(StepTelemetryInfo info)
    {
        // Use GenAI naming convention when applicable
        if (info.GenAiOperationName != null && info.GenAiRequestModel != null)
            return $"{info.GenAiOperationName} {info.GenAiRequestModel}";
        if (info.GenAiOperationName != null)
            return info.GenAiOperationName;
        return $"{info.StepType} {info.StepId}";
    }

    private static Activity? ResolveParentActivity(ITelemetrySpan parentSpan)
        => parentSpan switch
        {
            OTelWorkflowSpan workflowSpan => workflowSpan.Activity,
            OTelStepSpan stepSpan => stepSpan.Activity,
            _ => null
        };

    private static void ApplyWorkflowSourceTags(Activity activity, WorkflowTelemetryInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.SourceText))
            return;

        var source = WorkflowTelemetrySourceFormatter.Format(info.SourceText);

        activity.SetTag("gnougo-flow.workflow.source.format", string.IsNullOrWhiteSpace(info.SourceFormat) ? "yaml" : info.SourceFormat);
        activity.SetTag("gnougo-flow.workflow.source.length", source.OriginalLength);
        activity.SetTag("gnougo-flow.workflow.source.redacted_length", source.RedactedLength);
        activity.SetTag("gnougo-flow.workflow.source.truncated", source.Truncated);
        activity.SetTag("gnougo-flow.workflow.source.redacted", source.Redacted);
        activity.SetTag("gnougo-flow.workflow.source.limit", WorkflowTelemetrySourceFormatter.DefaultSourceAttributeLimit);
        activity.SetTag("gnougo-flow.workflow.source", source.Text);
    }

    public void Dispose()
    {
        _source.Dispose();
        _meter.Dispose();
    }

    // -- Span wrappers --

    private sealed class OTelWorkflowSpan(Activity? activity) : IWorkflowSpan
    {
        public Activity? Activity { get; } = activity;
        public void Dispose() => Activity?.Dispose();
    }

    private sealed class OTelStepSpan(Activity? activity) : IStepSpan
    {
        public Activity? Activity { get; } = activity;
        public void SetAttribute(string key, object? value) => Activity?.SetTag(key, value);
        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            if (Activity == null) return;
            if (attributes != null)
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

