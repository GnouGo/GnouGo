using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowTelemetryAdapterTests
{
    [Fact]
    public void CompositeWorkflowTelemetry_ForwardsInternalSpansToBothPipelines()
    {
        var streaming = new RecordingWorkflowTelemetry("streaming");
        var otel = new RecordingWorkflowTelemetry("otel");
        var telemetry = new CompositeWorkflowTelemetry(streaming, otel);

        var workflowSpan = telemetry.WorkflowStart(new WorkflowTelemetryInfo { WorkflowName = "main" });
        var stepSpan = telemetry.StepStart(workflowSpan, new StepTelemetryInfo
        {
            StepId = "plan",
            StepType = "workflow.plan"
        });

        var childSpan = telemetry.SpanStart(stepSpan, new TelemetrySpanInfo
        {
            Name = "workflow.plan.generate",
            Phase = "generation",
            StepId = "plan",
            StepType = "workflow.plan"
        });
        childSpan.SetAttribute("gen_ai.request.model", "test-model");
        childSpan.AddEvent("gen_ai.content.prompt", [
            new("gen_ai.prompt", "hello")
        ]);
        telemetry.SpanEnd(childSpan, new TelemetrySpanResultInfo
        {
            Success = true,
            Duration = TimeSpan.FromMilliseconds(42)
        });

        Assert.Contains(streaming.Events, e => e == "streaming:SpanStart:workflow.plan.generate");
        Assert.Contains(otel.Events, e => e == "otel:SpanStart:workflow.plan.generate");
        Assert.Contains(streaming.Events, e => e == "streaming:SetAttribute:gen_ai.request.model=test-model");
        Assert.Contains(otel.Events, e => e == "otel:SetAttribute:gen_ai.request.model=test-model");
        Assert.Contains(streaming.Events, e => e == "streaming:AddEvent:gen_ai.content.prompt");
        Assert.Contains(otel.Events, e => e == "otel:AddEvent:gen_ai.content.prompt");
        Assert.Contains(streaming.Events, e => e == "streaming:SpanEnd:True");
        Assert.Contains(otel.Events, e => e == "otel:SpanEnd:True");
    }

    [Fact]
    public void AgentStreamingTelemetry_EmitsLiveStepAndSpanEvents()
    {
        var events = new List<SmartFlowEvent>();
        var telemetry = new AgentStreamingTelemetry(events.Add);
        var workflowSpan = telemetry.WorkflowStart(new WorkflowTelemetryInfo { WorkflowName = "main" });
        var stepSpan = telemetry.StepStart(workflowSpan, new StepTelemetryInfo
        {
            StepId = "plan",
            StepType = "workflow.plan",
            CallDepth = 1
        });

        stepSpan.SetAttribute("gnougo-flow.plan.mode", "generate");
        stepSpan.AddEvent("gnougo-flow.step.thinking", [
            new("gnougo-flow.thinking.level", "progress"),
            new("gnougo-flow.thinking.message", "Generating workflow plan")
        ]);
        stepSpan.AddEvent("gnougo-flow.workflow_route.inputs_extracted", [
            new("gnougo-flow.workflow_route.candidate.id", "local:docs"),
            new("gnougo-flow.workflow_route.workflow.name", "docs"),
            new("gnougo-flow.workflow_route.arguments", "{\"query\":\"hello\"}")
        ]);
        stepSpan.AddEvent("gnougo-flow.step.thinking", [
            new("gnougo-flow.thinking.level", "progress"),
            new("gnougo-flow.thinking.message", "Triggering workflow 'docs' with inputs {\"query\":\"hello\"}"),
            new("gnougo-flow.thinking.source", "workflow.route"),
            new("gnougo-flow.workflow_route.candidate.id", "local:docs"),
            new("gnougo-flow.workflow_route.workflow.name", "docs")
        ]);

        var childSpan = telemetry.SpanStart(stepSpan, new TelemetrySpanInfo
        {
            Name = "workflow.plan.generate",
            Phase = "generation",
            StepId = "plan",
            StepType = "workflow.plan",
            Attributes = [
                new("gen_ai.operation.name", "chat")
            ]
        });
        childSpan.AddEvent("gen_ai.content.prompt", [
            new("gen_ai.prompt", "hello")
        ]);
        telemetry.SpanEnd(childSpan, new TelemetrySpanResultInfo
        {
            Success = true,
            Duration = TimeSpan.FromMilliseconds(12)
        });
        telemetry.StepEnd(stepSpan, new StepResultInfo
        {
            Status = StepStatus.Succeeded,
            Duration = TimeSpan.FromMilliseconds(20)
        });

        Assert.Contains(events, e => e.Type == "telemetry.workflow.start");
        Assert.Contains(events, e => e.Type == "telemetry.step.start" && Json(e)["step.id"]!.GetValue<string>() == "plan");
        Assert.Contains(events, e => e.Type == "telemetry.step.attribute" && Json(e)["key"]!.GetValue<string>() == "gnougo-flow.plan.mode");
        Assert.Contains(events, e => e.Type == "thinking:progress" && e.Text == "Generating workflow plan");
        Assert.Contains(events, e => e.Type == "thinking:progress" && e.Text == "Triggering workflow 'docs' with inputs {\"query\":\"hello\"}");
        var routeInputs = Assert.Single(events, e => e.Type == "workflow.route.inputs_extracted");
        Assert.Equal("local:docs", Json(routeInputs)["attributes"]!["gnougo-flow.workflow_route.candidate.id"]!.GetValue<string>());
        Assert.Equal("{\"query\":\"hello\"}", Json(routeInputs)["attributes"]!["gnougo-flow.workflow_route.arguments"]!.GetValue<string>());
        Assert.Contains(events, e => e.Type == "telemetry.step.event" && Json(e)["event.name"]!.GetValue<string>() == "gnougo-flow.step.thinking");
        Assert.Contains(events, e => e.Type == "telemetry.span.start" && Json(e)["span.name"]!.GetValue<string>() == "workflow.plan.generate");
        Assert.Contains(events, e => e.Type == "telemetry.span.event" && Json(e)["event.name"]!.GetValue<string>() == "gen_ai.content.prompt");
        Assert.Contains(events, e => e.Type == "telemetry.span.end" && Json(e)["success"]!.GetValue<bool>());
        Assert.Contains(events, e => e.Type == "telemetry.step.end" && Json(e)["status"]!.GetValue<string>() == StepStatus.Succeeded.ToString());
    }

    private static JsonNode Json(SmartFlowEvent evt)
    {
        Assert.False(string.IsNullOrWhiteSpace(evt.Text));
        return JsonNode.Parse(evt.Text!)!;
    }

    private sealed class RecordingWorkflowTelemetry(string name) : IWorkflowTelemetry
    {
        public List<string> Events { get; } = [];

        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
        {
            Events.Add($"{name}:WorkflowStart:{info.WorkflowName}");
            return new RecordingSpan(name, Events);
        }

        public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info)
        {
            Events.Add($"{name}:WorkflowStart:{info.WorkflowName}");
            return new RecordingSpan(name, Events);
        }

        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result)
            => Events.Add($"{name}:WorkflowEnd:{result.Success}");

        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
        {
            Events.Add($"{name}:StepStart:{info.StepId}");
            return new RecordingSpan(name, Events);
        }

        public void StepEnd(IStepSpan span, StepResultInfo result)
            => Events.Add($"{name}:StepEnd:{result.Status}");

        public ITelemetrySpan SpanStart(ITelemetrySpan parentSpan, TelemetrySpanInfo info)
        {
            Events.Add($"{name}:SpanStart:{info.Name}");
            return new RecordingSpan(name, Events);
        }

        public void SpanEnd(ITelemetrySpan span, TelemetrySpanResultInfo result)
            => Events.Add($"{name}:SpanEnd:{result.Success}");
    }

    private sealed class RecordingSpan(string name, List<string> events) : IWorkflowSpan, IStepSpan
    {
        public void SetAttribute(string key, object? value)
            => events.Add($"{name}:SetAttribute:{key}={value}");

        public void AddEvent(string eventName, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => events.Add($"{name}:AddEvent:{eventName}");

        public void Dispose() { }
    }
}
