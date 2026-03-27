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

    public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => new NoOpSpan();

    public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }

    public IStepSpan StepStart(IWorkflowSpan workflowSpan, StepTelemetryInfo info) =>
        new AgentStepSpan(info, _emit);

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
    }

    private sealed class NoOpSpan : IWorkflowSpan
    {
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

        public void SetAttribute(string key, object? value) { }

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
        {
            if (attributes is null) return;

            // Capture thinking/info/progress events from any executor (e.g. workflow.plan, emit, etc.)
            if (string.Equals(name, "gnougo-flow.step.thinking", StringComparison.Ordinal))
            {
                string? message = null;
                string level = "thinking";
                foreach (var attr in attributes)
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
                foreach (var attr in attributes)
                {
                    if (string.Equals(attr.Key, "gnougo-flow.human.request", StringComparison.Ordinal)
                        && attr.Value is string requestJson)
                    {
                        _emit(new SmartFlowEvent("human_input_request", requestJson));
                    }
                }
            }
        }

        public void Dispose() { }
    }
}

