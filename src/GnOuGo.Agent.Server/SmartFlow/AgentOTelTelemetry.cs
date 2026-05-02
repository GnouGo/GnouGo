using System.Diagnostics;
using System.Diagnostics.Metrics;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.AI.Core;
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
    public const string CorrelationIdTagName = "gnougo.agent.chat.correlation_id";
    public const string ConversationIdTagName = "gen_ai.conversation.id";

    private readonly ActivitySource _source;
    private readonly ActivityListener _listener;
    private readonly Meter _meter;
    private readonly CollectorTracePersistence _collectorTracePersistence;
    private readonly LocalTraceDebugStore _localTraceStore;

    /// <summary>
    /// Tracks the root chat activity for the current async flow. This provides a reliable fallback
    /// parent when <see cref="Activity.Current"/> is lost across async boundaries — notably when
    /// a workflow resumes from a <c>human.input</c> step whose completion arrives on an unrelated
    /// thread (TCS continuation), or when <see cref="Task.Run"/> / channel readers execute on
    /// thread-pool threads whose <see cref="Activity.Current"/> does not match the chat root.
    /// Assumes one chat message per logical async flow (AsyncLocal guarantees per-flow isolation,
    /// so concurrent chats on different requests are unaffected).
    /// </summary>
    private static readonly AsyncLocal<Activity?> _currentChatRoot = new();
    private readonly Counter<long>      _stepCounter;
    private readonly Histogram<double>  _stepDuration;
    private readonly Counter<long>      _tokenUsage;
    private readonly Histogram<double>  _workflowDuration;

    public AgentOTelTelemetry(
        CollectorTracePersistence collectorTracePersistence,
        LocalTraceDebugStore localTraceStore)
    {
        _collectorTracePersistence = collectorTracePersistence;
        _localTraceStore = localTraceStore;
        _source = new ActivitySource(ActivitySourceName, "1.0.0");
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _localTraceStore.Track(activity),
            ActivityStopped = activity => _localTraceStore.Complete(activity)
        };
        ActivitySource.AddActivityListener(_listener);
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

    public ChatTraceScope StartChatMessageActivity(string correlationId, string task)
    {
        var trimmed = task.Trim();
        var isCommand = trimmed.StartsWith("/", StringComparison.Ordinal);
        var activity = StartRootChatActivity(isCommand ? "chat.command" : "chat.message", correlationId);

        activity.SetTag(CorrelationIdTagName, correlationId);
        activity.SetTag(ConversationIdTagName, correlationId);
        activity.SetTag("gnougo.agent.chat.kind", isCommand ? "command" : "prompt");
        activity.SetTag("gnougo.agent.chat.input_length", task.Length);

        if (isCommand)
        {
            var commandName = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(commandName))
                activity.SetTag("gnougo.agent.chat.command", commandName);
        }

        activity.AddBaggage(CorrelationIdTagName, correlationId);
        activity.AddBaggage(ConversationIdTagName, correlationId);
        return new ChatTraceScope(activity, _collectorTracePersistence, _currentChatRoot);
    }

    public ActivityScope StartActivityScope(string name, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = StartActivity(name, kind, ResolveImplicitParent());
        ApplyCorrelationTags(activity);
        return new ActivityScope(activity, _collectorTracePersistence);
    }

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
    {
        var a = StartActivity("workflow", ActivityKind.Internal, ResolveImplicitParent());
        ApplyCorrelationTags(a);
        a.SetTag("gnougo-flow.workflow.name", info.WorkflowName);
        if (info.DocumentName is not null) a.SetTag("gnougo-flow.document.name", info.DocumentName);
        ApplyWorkflowSourceTags(a, info);
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
        _collectorTracePersistence.Persist(ws.Activity);
    }

    public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
    {
        var parent = ResolveParentActivity(parentSpan) ?? ResolveImplicitParent();
        var a = StartActivity(SpanName(info), ActivityKind.Client, parent);
        ApplyCorrelationTags(a);
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
            var cost = ModelMetadataCatalog.EstimateCost(model,
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

        _collectorTracePersistence.Persist(ss.Activity);
    }

    public ITelemetrySpan SpanStart(ITelemetrySpan parentSpan, TelemetrySpanInfo info)
    {
        var parent = ResolveParentActivity(parentSpan) ?? ResolveImplicitParent();
        var activity = StartActivity(info.Name, ActivityKind.Internal, parent);
        ApplyCorrelationTags(activity);
        ApplySpanInfo(activity, info);
        return new GenericSpan(activity);
    }

    public void SpanEnd(ITelemetrySpan span, TelemetrySpanResultInfo result)
    {
        if (span is not GenericSpan genericSpan || genericSpan.Activity is null) return;
        genericSpan.Activity.SetTag("gnougo-flow.span.duration_ms", result.Duration.TotalMilliseconds);
        if (result.Success)
        {
            genericSpan.Activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            genericSpan.Activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
            genericSpan.Activity.SetTag("error.type", result.ErrorType);
            genericSpan.Activity.SetTag("error.message", result.ErrorMessage);
        }
        _collectorTracePersistence.Persist(genericSpan.Activity);
    }

    public void Dispose()
    {
        _listener.Dispose();
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

    private static void ApplyCorrelationTags(Activity activity)
    {
        var correlationId = activity.GetBaggageItem(CorrelationIdTagName);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            activity.SetTag(CorrelationIdTagName, correlationId);
            activity.SetTag(ConversationIdTagName, correlationId);
        }
    }

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

    private static void PropagateCorrelationFromParent(Activity parent, Activity activity)
    {
        var correlationId = parent.GetTagItem(CorrelationIdTagName) as string
            ?? parent.GetBaggageItem(CorrelationIdTagName)
            ?? parent.GetTagItem(ConversationIdTagName) as string
            ?? parent.GetBaggageItem(ConversationIdTagName);

        if (string.IsNullOrWhiteSpace(correlationId))
            return;

        activity.AddBaggage(CorrelationIdTagName, correlationId);
        activity.AddBaggage(ConversationIdTagName, correlationId);
        activity.SetTag(CorrelationIdTagName, correlationId);
        activity.SetTag(ConversationIdTagName, correlationId);
    }

    private static Activity? ResolveParentActivity(ITelemetrySpan parentSpan)
        => parentSpan switch
        {
            WfSpan workflowSpan => workflowSpan.Activity,
            StSpan stepSpan => stepSpan.Activity,
            GenericSpan genericSpan => genericSpan.Activity,
            _ => null
        };

    private static void ApplySpanInfo(Activity activity, TelemetrySpanInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Phase)) activity.SetTag("gnougo-flow.plan.phase", info.Phase);
        if (!string.IsNullOrWhiteSpace(info.StepId)) activity.SetTag("gnougo-flow.step.id", info.StepId);
        if (!string.IsNullOrWhiteSpace(info.StepType)) activity.SetTag("gnougo-flow.step.type", info.StepType);
        if (info.CallDepth.HasValue) activity.SetTag("gnougo-flow.step.call_depth", info.CallDepth.Value);
        if (info.Attributes is null) return;
        foreach (var kv in info.Attributes)
            activity.SetTag(kv.Key, kv.Value);
    }

    /// <summary>
    /// Returns the best-effort implicit parent activity for spans that don't receive an
    /// explicit parent (e.g., the root <c>workflow</c> span). Prefers <see cref="Activity.Current"/>
    /// when it belongs to the same trace as the captured chat root; otherwise falls back to
    /// the chat root itself. This prevents orphan traces when async continuations resume on
    /// thread-pool threads with a mismatched or null <see cref="Activity.Current"/>.
    /// </summary>
    private static Activity? ResolveImplicitParent()
    {
        var chatRoot = _currentChatRoot.Value;
        var current = Activity.Current;
        if (chatRoot is null)
            return current;
        if (current is null)
            return chatRoot;
        return current.TraceId == chatRoot.TraceId ? current : chatRoot;
    }

    private Activity StartActivity(string name, ActivityKind kind, Activity? parent)
    {
        Activity? activity;
        if (parent is not null)
        {
            activity = _source.StartActivity(
                name,
                kind,
                new ActivityContext(parent.TraceId, parent.SpanId, ActivityTraceFlags.Recorded));

            if (activity is null)
            {
                activity = new Activity(name)
                    .SetIdFormat(ActivityIdFormat.W3C)
                    .SetParentId(parent.TraceId, parent.SpanId, ActivityTraceFlags.Recorded)
                    .Start();
            }

            PropagateCorrelationFromParent(parent, activity);
        }
        else
        {
            activity = _source.StartActivity(name, kind)
                ?? new Activity(name).SetIdFormat(ActivityIdFormat.W3C).Start();
        }

        return activity;
    }

    private Activity StartRootChatActivity(string name, string correlationId)
    {
        if (correlationId.Length == 32)
        {
            try
            {
                var traceId = ActivityTraceId.CreateFromString(correlationId.AsSpan());
                var syntheticParent = ActivitySpanId.CreateRandom();
                var context = new ActivityContext(traceId, syntheticParent, ActivityTraceFlags.Recorded);
                var activity = _source.StartActivity(name, ActivityKind.Internal, context);
                if (activity is not null)
                    return activity;

                return new Activity(name)
                    .SetIdFormat(ActivityIdFormat.W3C)
                    .SetParentId(traceId, syntheticParent, ActivityTraceFlags.Recorded)
                    .Start();
            }
            catch
            {
                // Fallback below when the correlation identifier is not valid W3C trace-id hex.
            }
        }

        return StartActivity(name, ActivityKind.Internal, parent: null);
    }

    // ── Span wrappers ────────────────────────────────────────────────────────

    public sealed class ChatTraceScope : IDisposable
    {
        private readonly CollectorTracePersistence _collectorTracePersistence;
        private readonly AsyncLocal<Activity?> _chatRootSlot;
        private readonly Activity? _previousChatRoot;

        public ChatTraceScope(Activity activity, CollectorTracePersistence collectorTracePersistence, AsyncLocal<Activity?> chatRootSlot)
        {
            Activity = activity;
            _collectorTracePersistence = collectorTracePersistence;
            _chatRootSlot = chatRootSlot;
            _previousChatRoot = chatRootSlot.Value;
            chatRootSlot.Value = activity;
        }

        public Activity Activity { get; }
        public string TraceId => Activity.TraceId.ToHexString();

        public void SetStatus(ActivityStatusCode code, string? description = null)
        {
            Activity.SetStatus(code, description);
        }

        public void Dispose()
        {
            _collectorTracePersistence.Persist(Activity);
            _chatRootSlot.Value = _previousChatRoot;
            Activity.Dispose();
        }
    }

    public sealed class ActivityScope(Activity activity, CollectorTracePersistence collectorTracePersistence) : IDisposable
    {
        public Activity Activity { get; } = activity;

        public void SetTag(string key, object? value)
            => Activity.SetTag(key, value);

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            if (attributes is not null)
            {
                var tags = new ActivityTagsCollection();
                foreach (var kv in attributes)
                    tags[kv.Key] = kv.Value;
                Activity.AddEvent(new ActivityEvent(name, tags: tags));
                return;
            }

            Activity.AddEvent(new ActivityEvent(name));
        }

        public void SetStatus(ActivityStatusCode code, string? description = null)
            => Activity.SetStatus(code, description);

        public void Dispose()
        {
            collectorTracePersistence.Persist(Activity);
            Activity.Dispose();
        }
    }

    private sealed class WfSpan(Activity? activity) : IWorkflowSpan
    {
        public Activity? Activity { get; } = activity;
        public void SetAttribute(string key, object? value) => Activity?.SetTag(key, value);
        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => AddActivityEvent(Activity, name, attributes);
        public void Dispose() => Activity?.Dispose();
    }

    private sealed class GenericSpan(Activity? activity) : ITelemetrySpan
    {
        public Activity? Activity { get; } = activity;
        public void SetAttribute(string key, object? value) => Activity?.SetTag(key, value);
        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => AddActivityEvent(Activity, name, attributes);
        public void Dispose() => Activity?.Dispose();
    }

    private static void AddActivityEvent(Activity? activity, string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes)
    {
        if (activity is null) return;
        if (attributes is not null)
        {
            var tags = new ActivityTagsCollection();
            foreach (var kv in attributes) tags[kv.Key] = kv.Value;
            activity.AddEvent(new ActivityEvent(name, tags: tags));
        }
        else
        {
            activity.AddEvent(new ActivityEvent(name));
        }
    }

    private sealed class StSpan(Activity? activity) : IStepSpan
    {
        public Activity? Activity { get; } = activity;

        public void SetAttribute(string key, object? value)
        {
            if (Activity is null) return;
            Activity.SetTag(key, value);
        }

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

