using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Lightweight telemetry implementation that captures thinking events (from any executor)
/// and LLM chunks, forwarding them as <see cref="SmartFlowEvent"/> to the streaming channel.
/// </summary>
public sealed class AgentStreamingTelemetry : IWorkflowTelemetry
{
    private readonly Action<SmartFlowEvent> _emit;

    public AgentStreamingTelemetry(Action<SmartFlowEvent> emit)
    {
        _emit = emit;
    }

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
    {
        _emit(new SmartFlowEvent("telemetry.workflow.start", Payload([
            new("workflow.name", info.WorkflowName),
            new("document.name", info.DocumentName)
        ])));
        return new AgentWorkflowSpan(info, _emit);
    }

    public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info) => WorkflowStart(info);

    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
    {
        _emit(new SmartFlowEvent("telemetry.workflow.end", Payload([
            new("success", result.Success),
            new("steps.executed", result.StepsExecuted),
            new("duration.ms", result.Duration.TotalMilliseconds),
            new("error.code", result.ErrorCode),
            new("error.message", result.ErrorMessage)
        ])));
    }

    public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
    {
        _emit(new SmartFlowEvent("telemetry.step.start", Payload([
            new("step.id", info.StepId),
            new("step.type", info.StepType),
            new("call.depth", info.CallDepth),
            new("gen_ai.operation.name", info.GenAiOperationName),
            new("gen_ai.system", info.GenAiSystem),
            new("gen_ai.request.model", info.GenAiRequestModel),
            new("mcp.server.name", info.McpServerName),
            new("mcp.method.name", info.McpMethodName),
            new("mcp.kind", info.McpKind)
        ])));
        return new AgentStepSpan(info, _emit);
    }

    public void StepEnd(IStepSpan span, StepResultInfo result)
    {
        if (span is not AgentStepSpan agentSpan)
            return;


        // Capture LLM step completions for streaming text chunks
        if (string.Equals(agentSpan.Info.StepType, "llm.call", StringComparison.OrdinalIgnoreCase)
            && result.Output is JsonObject llmOutput)
        {
            var text = llmOutput["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _emit(new SmartFlowEvent("llm.chunk", text));
            }
        }

        _emit(new SmartFlowEvent("telemetry.step.end", Payload([
            new("step.id", agentSpan.Info.StepId),
            new("step.type", agentSpan.Info.StepType),
            new("status", result.Status.ToString()),
            new("duration.ms", result.Duration.TotalMilliseconds),
            new("error.code", result.ErrorCode),
            new("error.message", result.ErrorMessage),
            new("gen_ai.response.finish_reason", result.GenAiFinishReason),
            new("gen_ai.usage.input_tokens", result.GenAiInputTokens),
            new("gen_ai.usage.output_tokens", result.GenAiOutputTokens)
        ])));
    }

    public ITelemetrySpan SpanStart(ITelemetrySpan parentSpan, TelemetrySpanInfo info)
    {
        _emit(new SmartFlowEvent("telemetry.span.start", Payload([
            new("span.name", info.Name),
            new("phase", info.Phase),
            new("step.id", info.StepId),
            new("step.type", info.StepType),
            new("call.depth", info.CallDepth),
            new("attributes", AttributesObject(info.Attributes))
        ])));
        return new AgentInternalSpan(info, _emit);
    }

    public void SpanEnd(ITelemetrySpan span, TelemetrySpanResultInfo result)
    {
        if (span is not AgentInternalSpan agentSpan)
            return;

        _emit(new SmartFlowEvent("telemetry.span.end", Payload([
            new("span.name", agentSpan.Info.Name),
            new("phase", agentSpan.Info.Phase),
            new("step.id", agentSpan.Info.StepId),
            new("step.type", agentSpan.Info.StepType),
            new("success", result.Success),
            new("duration.ms", result.Duration.TotalMilliseconds),
            new("error.type", result.ErrorType),
            new("error.message", result.ErrorMessage)
        ])));
    }

    private sealed class AgentWorkflowSpan(WorkflowTelemetryInfo info, Action<SmartFlowEvent> emit) : IWorkflowSpan
    {
        public void SetAttribute(string key, object? value)
            => emit(new SmartFlowEvent("telemetry.workflow.attribute", Payload([
                new("workflow.name", info.WorkflowName),
                new("key", key),
                new("value", ToNode(value))
            ])));

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => emit(new SmartFlowEvent("telemetry.workflow.event", Payload([
                new("workflow.name", info.WorkflowName),
                new("event.name", name),
                new("attributes", AttributesObject(attributes))
            ])));

        public void Dispose() { }
    }

    private sealed class AgentStepSpan : IStepSpan
    {
        public StepTelemetryInfo Info { get; }
        private readonly Action<SmartFlowEvent> _emit;

        public AgentStepSpan(StepTelemetryInfo info, Action<SmartFlowEvent> emit)
        {
            Info = info;
            _emit = emit;
        }

        public void SetAttribute(string key, object? value)
            => _emit(new SmartFlowEvent("telemetry.step.attribute", Payload([
                new("step.id", Info.StepId),
                new("step.type", Info.StepType),
                new("key", key),
                new("value", ToNode(value))
            ])));

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            // Capture thinking/info/progress events from any executor (e.g. workflow.plan, emit, etc.)
            if (string.Equals(name, "gnougo-flow.step.thinking", StringComparison.Ordinal))
            {
                string? message = null;
                string level = "thinking";
                foreach (var attr in attributes ?? [])
                {
                    if (string.Equals(attr.Key, "gnougo-flow.thinking.message", StringComparison.Ordinal)
                        && attr.Value is string msg)
                        message = msg;
                    else if (string.Equals(attr.Key, "gnougo-flow.thinking.level", StringComparison.Ordinal)
                             && attr.Value is string lvl)
                        level = lvl;
                }
                if (!string.IsNullOrWhiteSpace(message))
                    _emit(new SmartFlowEvent($"thinking:{level}", message));
            }

            // Capture human.input waiting events and forward to the UI
            if (string.Equals(name, "gnougo-flow.step.waiting_for_human", StringComparison.Ordinal))
            {
                foreach (var attr in attributes ?? [])
                {
                    if (string.Equals(attr.Key, "gnougo-flow.human.request", StringComparison.Ordinal)
                        && attr.Value is string requestJson)
                    {
                        _emit(new SmartFlowEvent("human_input_request", requestJson));
                    }
                }
            }

            _emit(new SmartFlowEvent("telemetry.step.event", Payload([
                new("step.id", Info.StepId),
                new("step.type", Info.StepType),
                new("event.name", name),
                new("attributes", AttributesObject(attributes))
            ])));
        }

        public void Dispose() { }
    }

    private sealed class AgentInternalSpan(TelemetrySpanInfo info, Action<SmartFlowEvent> emit) : ITelemetrySpan
    {
        public TelemetrySpanInfo Info { get; } = info;

        public void SetAttribute(string key, object? value)
            => emit(new SmartFlowEvent("telemetry.span.attribute", Payload([
                new("span.name", Info.Name),
                new("phase", Info.Phase),
                new("step.id", Info.StepId),
                new("step.type", Info.StepType),
                new("key", key),
                new("value", ToNode(value))
            ])));

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => emit(new SmartFlowEvent("telemetry.span.event", Payload([
                new("span.name", Info.Name),
                new("phase", Info.Phase),
                new("step.id", Info.StepId),
                new("step.type", Info.StepType),
                new("event.name", name),
                new("attributes", AttributesObject(attributes))
            ])));

        public void Dispose() { }
    }

    private static string Payload(IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var obj = new JsonObject();
        foreach (var kv in values)
        {
            if (kv.Value is null)
                continue;

            obj[kv.Key] = ToNode(kv.Value);
        }

        return obj.ToJsonString();
    }

    private static JsonObject AttributesObject(IReadOnlyList<KeyValuePair<string, object?>>? attributes)
    {
        var obj = new JsonObject();
        if (attributes is null)
            return obj;

        foreach (var kv in attributes)
            obj[kv.Key] = ToNode(kv.Value);

        return obj;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal d => JsonValue.Create(d),
        TimeSpan ts => JsonValue.Create(ts.TotalMilliseconds),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("O")),
        DateTime dt => JsonValue.Create(dt.ToString("O")),
        _ => JsonValue.Create(value.ToString())
    };
}
