using System.Text.Json.Nodes;
using GnOuGo.Assets.Animation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Streams workflow telemetry to chat and, when enabled, to the neutral live animation bridge.
/// </summary>
public sealed class AgentStreamingTelemetry : IWorkflowTelemetry
{
    private readonly Action<SmartFlowEvent> _emit;
    private readonly AgentWorkflowAnimationBridge? _animation;
    private long _workflowSequence;
    private long _stepSequence;

    public AgentStreamingTelemetry(
        Action<SmartFlowEvent> emit,
        AgentWorkflowAnimationBridge? animation = null)
    {
        _emit = emit;
        _animation = animation;
    }

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
        => StartWorkflow(parentSpan: null, info);

    public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info)
        => StartWorkflow(parentSpan, info);

    private IWorkflowSpan StartWorkflow(ITelemetrySpan? parentSpan, WorkflowTelemetryInfo info)
    {
        var instanceId = $"workflow-{Interlocked.Increment(ref _workflowSequence):D4}";
        var parentWorkflowInstanceId = FindWorkflowInstanceId(parentSpan);
        var callerStepOccurrenceId = (parentSpan as AgentStepSpan)?.OccurrenceId;
        _emit(new SmartFlowEvent("telemetry.workflow.start", Payload([
            new("workflow.instance.id", instanceId),
            new("workflow.parent.instance.id", parentWorkflowInstanceId),
            new("caller.step.occurrence.id", callerStepOccurrenceId),
            new("workflow.name", info.WorkflowName),
            new("document.name", info.DocumentName)
        ])));
        _animation?.Apply(new AnimationExecutionSignal
        {
            Sequence = _workflowSequence,
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = instanceId,
            ParentWorkflowInstanceId = parentWorkflowInstanceId,
            CallerStepOccurrenceId = callerStepOccurrenceId,
            WorkflowName = info.WorkflowName,
            SourceText = info.SourceText,
            Status = SimulationStatus.Running
        });
        return new AgentWorkflowSpan(
            info,
            instanceId,
            parentWorkflowInstanceId,
            callerStepOccurrenceId,
            _emit);
    }

    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
    {
        if (span is not AgentWorkflowSpan workflowSpan)
            return;

        _emit(new SmartFlowEvent("telemetry.workflow.end", Payload([
            new("workflow.instance.id", workflowSpan.InstanceId),
            new("workflow.parent.instance.id", workflowSpan.ParentWorkflowInstanceId),
            new("success", result.Success),
            new("steps.executed", result.StepsExecuted),
            new("duration.ms", result.Duration.TotalMilliseconds),
            new("error.code", result.ErrorCode),
            new("error.message", result.ErrorMessage)
        ])));
        _animation?.Apply(new AnimationExecutionSignal
        {
            Sequence = _workflowSequence,
            Kind = AnimationExecutionSignalKind.WorkflowCompleted,
            WorkflowInstanceId = workflowSpan.InstanceId,
            ParentWorkflowInstanceId = workflowSpan.ParentWorkflowInstanceId,
            CallerStepOccurrenceId = workflowSpan.CallerStepOccurrenceId,
            WorkflowName = workflowSpan.Info.WorkflowName,
            Status = result.Success ? SimulationStatus.Succeeded : SimulationStatus.Failed,
            Message = result.ErrorMessage
        });
    }

    public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
    {
        var workflowInstanceId = FindWorkflowInstanceId(parentSpan);
        var occurrenceId = $"step-{Interlocked.Increment(ref _stepSequence):D6}";
        _emit(new SmartFlowEvent("telemetry.step.start", Payload([
            new("workflow.instance.id", workflowInstanceId),
            new("step.occurrence.id", occurrenceId),
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
        _animation?.Apply(new AnimationExecutionSignal
        {
            Sequence = _stepSequence,
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = workflowInstanceId,
            StepOccurrenceId = occurrenceId,
            StepId = info.StepId,
            StepType = info.StepType,
            Status = SimulationStatus.Running
        });
        return new AgentStepSpan(
            info,
            workflowInstanceId,
            occurrenceId,
            _animation,
            _emit);
    }

    public void StepEnd(IStepSpan span, StepResultInfo result)
    {
        if (span is not AgentStepSpan agentSpan)
            return;

        if (string.Equals(agentSpan.Info.StepType, "llm.call", StringComparison.OrdinalIgnoreCase)
            && result.Output is JsonObject llmOutput)
        {
            var text = llmOutput["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
                _emit(new SmartFlowEvent("llm.chunk", text));
        }

        _emit(new SmartFlowEvent("telemetry.step.end", Payload([
            new("workflow.instance.id", agentSpan.WorkflowInstanceId),
            new("step.occurrence.id", agentSpan.OccurrenceId),
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
        _animation?.Apply(new AnimationExecutionSignal
        {
            Sequence = _stepSequence,
            Kind = AnimationExecutionSignalKind.StepCompleted,
            WorkflowInstanceId = agentSpan.WorkflowInstanceId,
            StepOccurrenceId = agentSpan.OccurrenceId,
            StepId = agentSpan.Info.StepId,
            StepType = agentSpan.Info.StepType,
            Status = result.Status switch
            {
                StepStatus.Succeeded => SimulationStatus.Succeeded,
                StepStatus.Skipped => SimulationStatus.Skipped,
                _ => SimulationStatus.Failed
            },
            Message = result.ErrorMessage
        });
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

    private sealed class AgentWorkflowSpan : IWorkflowSpan
    {
        private readonly Action<SmartFlowEvent> _emit;

        public AgentWorkflowSpan(
            WorkflowTelemetryInfo info,
            string instanceId,
            string? parentWorkflowInstanceId,
            string? callerStepOccurrenceId,
            Action<SmartFlowEvent> emit)
        {
            Info = info;
            InstanceId = instanceId;
            ParentWorkflowInstanceId = parentWorkflowInstanceId;
            CallerStepOccurrenceId = callerStepOccurrenceId;
            _emit = emit;
        }

        public WorkflowTelemetryInfo Info { get; }
        public string InstanceId { get; }
        public string? ParentWorkflowInstanceId { get; }
        public string? CallerStepOccurrenceId { get; }

        public void SetAttribute(string key, object? value)
            => _emit(new SmartFlowEvent("telemetry.workflow.attribute", Payload([
                new("workflow.instance.id", InstanceId),
                new("workflow.name", Info.WorkflowName),
                new("key", key),
                new("value", ToNode(value))
            ])));

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => _emit(new SmartFlowEvent("telemetry.workflow.event", Payload([
                new("workflow.instance.id", InstanceId),
                new("workflow.name", Info.WorkflowName),
                new("event.name", name),
                new("attributes", AttributesObject(attributes))
            ])));

        public void Dispose() { }
    }

    private sealed class AgentStepSpan : IStepSpan
    {
        public StepTelemetryInfo Info { get; }
        public string? WorkflowInstanceId { get; }
        public string OccurrenceId { get; }
        private readonly AgentWorkflowAnimationBridge? _animation;
        private readonly Action<SmartFlowEvent> _emit;

        public AgentStepSpan(
            StepTelemetryInfo info,
            string? workflowInstanceId,
            string occurrenceId,
            AgentWorkflowAnimationBridge? animation,
            Action<SmartFlowEvent> emit)
        {
            Info = info;
            WorkflowInstanceId = workflowInstanceId;
            OccurrenceId = occurrenceId;
            _animation = animation;
            _emit = emit;
        }

        public void SetAttribute(string key, object? value)
            => _emit(new SmartFlowEvent("telemetry.step.attribute", Payload([
                new("workflow.instance.id", WorkflowInstanceId),
                new("step.occurrence.id", OccurrenceId),
                new("step.id", Info.StepId),
                new("step.type", Info.StepType),
                new("key", key),
                new("value", ToNode(value))
            ])));

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            if (string.Equals(name, "gnougo-flow.step.thinking", StringComparison.Ordinal))
            {
                string? message = null;
                var level = "thinking";
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

            if (string.Equals(name, "gnougo-flow.step.waiting_for_human", StringComparison.Ordinal))
            {
                foreach (var attr in attributes ?? [])
                {
                    if (string.Equals(attr.Key, "gnougo-flow.human.request", StringComparison.Ordinal)
                        && attr.Value is string requestJson)
                    {
                        _emit(new SmartFlowEvent("human_input_request", requestJson));
                        _animation?.Apply(new AnimationExecutionSignal
                        {
                            Kind = AnimationExecutionSignalKind.HumanInputWaiting,
                            WorkflowInstanceId = WorkflowInstanceId,
                            StepOccurrenceId = OccurrenceId,
                            StepId = Info.StepId,
                            StepType = Info.StepType,
                            Status = SimulationStatus.Running,
                            Message = "Waiting for human input."
                        });
                    }
                }
            }

            if (string.Equals(name, "gnougo-flow.workflow_route.inputs_extracted", StringComparison.Ordinal))
            {
                _emit(new SmartFlowEvent("workflow.route.inputs_extracted", Payload([
                    new("attributes", AttributesObject(attributes))
                ])));
            }

            _emit(new SmartFlowEvent("telemetry.step.event", Payload([
                new("workflow.instance.id", WorkflowInstanceId),
                new("step.occurrence.id", OccurrenceId),
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

    private static string? FindWorkflowInstanceId(ITelemetrySpan? span) => span switch
    {
        AgentWorkflowSpan workflow => workflow.InstanceId,
        AgentStepSpan step => step.WorkflowInstanceId,
        _ => null
    };

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
