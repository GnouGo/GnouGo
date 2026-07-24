using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Assets.Animation;
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

    [Fact]
    public void AgentStreamingTelemetry_PreservesWorkflowHierarchyAndUniqueStepOccurrences()
    {
        var events = new List<SmartFlowEvent>();
        var telemetry = new AgentStreamingTelemetry(events.Add);
        var root = telemetry.WorkflowStart(new WorkflowTelemetryInfo { WorkflowName = "main" });
        var call = telemetry.StepStart(root, new StepTelemetryInfo
        {
            StepId = "same-id",
            StepType = "workflow.call"
        });
        var child = telemetry.WorkflowStart(call, new WorkflowTelemetryInfo { WorkflowName = "child" });
        var childStep = telemetry.StepStart(child, new StepTelemetryInfo
        {
            StepId = "same-id",
            StepType = "llm.call"
        });

        var workflowStarts = events
            .Where(item => item.Type == "telemetry.workflow.start")
            .Select(Json)
            .ToArray();
        var stepStarts = events
            .Where(item => item.Type == "telemetry.step.start")
            .Select(Json)
            .ToArray();

        Assert.Equal("workflow-0001", workflowStarts[0]["workflow.instance.id"]!.GetValue<string>());
        Assert.Equal("workflow-0001", workflowStarts[1]["workflow.parent.instance.id"]!.GetValue<string>());
        Assert.Equal(
            stepStarts[0]["step.occurrence.id"]!.GetValue<string>(),
            workflowStarts[1]["caller.step.occurrence.id"]!.GetValue<string>());
        Assert.NotEqual(
            stepStarts[0]["step.occurrence.id"]!.GetValue<string>(),
            stepStarts[1]["step.occurrence.id"]!.GetValue<string>());

        telemetry.StepEnd(childStep, new StepResultInfo { Status = StepStatus.Succeeded });
    }

    [Fact]
    public void AnimationBridge_EmitsPreparedSceneAndLiveEventsWithoutWorkflowSource()
    {
        var events = new List<SmartFlowEvent>();
        var bridge = AgentWorkflowAnimationBridge.Create(
            """
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: model, type: llm.call }
            """,
            "main",
            "00112233445566778899aabbccddeeff",
            events.Add,
            out var prepared);
        var telemetry = new AgentStreamingTelemetry(events.Add, bridge);
        var workflow = telemetry.WorkflowStart(new WorkflowTelemetryInfo
        {
            WorkflowName = "main",
            SourceText = "secret workflow source that must not reach the browser"
        });
        var step = telemetry.StepStart(workflow, new StepTelemetryInfo
        {
            StepId = "model",
            StepType = "llm.call"
        });
        telemetry.StepEnd(step, new StepResultInfo { Status = StepStatus.Succeeded });
        telemetry.WorkflowEnd(workflow, new WorkflowResultInfo { Success = true });

        Assert.Equal("animation.prepared", prepared.Type);
        Assert.NotNull(prepared.Animation?.Prepared?.Svg);
        Assert.Contains(events, item => item.Type == "animation.event"
                                        && item.Animation?.Event?.Type == "step.started");
        Assert.Contains(events, item => item.Type == "animation.event"
                                        && item.Animation?.Event?.Type == "simulation.completed");
        Assert.DoesNotContain(events, item =>
            item.Animation?.Prepared?.Svg.Contains("secret workflow source", StringComparison.Ordinal) == true
            || item.Animation?.ScenePatch?.SvgFragment.Contains("secret workflow source", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AnimationBridge_PreStartFailureDoesNotLeavePreparedSceneStatic()
    {
        var events = new List<SmartFlowEvent>();
        var bridge = AgentWorkflowAnimationBridge.Create(
            """
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: model, type: llm.call }
            """,
            "main",
            "1234567890abcdef1234567890abcdef",
            events.Add,
            out _);

        bridge.FailBeforeWorkflowStart("A required workflow input is missing.");

        var animationEvents = events
            .Where(item => item.Type == "animation.event")
            .Select(item => item.Animation?.Event)
            .OfType<SimulationEvent>()
            .ToArray();

        Assert.Contains(animationEvents, item => item.Type == SimulationEventTypes.ActorSpawned);
        Assert.Contains(animationEvents, item => item.Type == SimulationEventTypes.TaskDropped);
        Assert.Contains(animationEvents, item =>
            item.Type == SimulationEventTypes.SimulationCompleted
            && item.Status == SimulationStatus.Failed);
        Assert.Contains(animationEvents, item =>
            item.Message == "A required workflow input is missing.");
    }

    [Fact]
    public void CompositeTelemetry_RoutedChildKeepsLiveAnimationMovingAfterRootStart()
    {
        const string routingYaml = """
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: route, type: workflow.route }
              fallback_general:
                steps:
                  - { id: answer, type: llm.call }
            """;
        var events = new List<SmartFlowEvent>();
        var bridge = AgentWorkflowAnimationBridge.Create(
            routingYaml,
            "main",
            "ffeeddccbbaa99887766554433221100",
            events.Add,
            out _);
        var telemetry = new CompositeWorkflowTelemetry(
            new AgentStreamingTelemetry(events.Add, bridge),
            NullWorkflowTelemetry.Instance);

        var root = telemetry.WorkflowStart(new WorkflowTelemetryInfo
        {
            WorkflowName = "main",
            SourceText = routingYaml
        });
        var route = telemetry.StepStart(root, new StepTelemetryInfo
        {
            StepId = "route",
            StepType = "workflow.route"
        });
        var child = telemetry.WorkflowStart(route, new WorkflowTelemetryInfo
        {
            WorkflowName = "fallback_general",
            SourceText = routingYaml
        });
        var answer = telemetry.StepStart(child, new StepTelemetryInfo
        {
            StepId = "answer",
            StepType = "llm.call"
        });

        var animationEvents = events
            .Where(item => item.Type == "animation.event")
            .Select(item => item.Animation?.Event)
            .OfType<SimulationEvent>()
            .ToArray();
        var childStep = Assert.Single(animationEvents, item =>
            item.Type == SimulationEventTypes.StepStarted
            && item.WorkflowName == "fallback_general"
            && item.StepId == "answer");

        Assert.Contains(events, item => item.Type == "animation.scene.patch");
        Assert.Contains(animationEvents, item =>
            item.Type == SimulationEventTypes.ActorSpawned
            && item.WorkflowName == "fallback_general");
        Assert.NotNull(childStep.ActorId);
        Assert.NotNull(childStep.NodeId);
        Assert.NotNull(childStep.StationId);
        Assert.True(childStep.DurationMs >= 30_000);

        telemetry.StepEnd(answer, new StepResultInfo { Status = StepStatus.Succeeded });
        telemetry.WorkflowEnd(child, new WorkflowResultInfo { Success = true });
        telemetry.StepEnd(route, new StepResultInfo { Status = StepStatus.Succeeded });
        telemetry.WorkflowEnd(root, new WorkflowResultInfo { Success = true });

        Assert.Contains(events, item => item.Animation?.Event?.Type == SimulationEventTypes.SimulationCompleted);
    }

    [Fact]
    public void AnimationPayload_UsesSingleLineSourceGeneratedJson()
    {
        var payload = new AnimationStreamPayload(
            Event: new GnOuGo.Assets.Animation.SimulationEvent
            {
                Type = "step.started",
                StepId = "model",
                Status = GnOuGo.Assets.Animation.SimulationStatus.Failed,
                Message = "line one\nline two"
            });

        var json = JsonSerializer.Serialize(
            payload,
            AgentAnimationJsonContext.Default.AnimationStreamPayload);

        Assert.DoesNotContain('\n', json);
        Assert.Contains("\"stepId\":\"model\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPage_KeepsThinkingOutOfPersistedMessagesAndOrdersAnimationBeforeAnswer()
    {
        var root = FindRepositoryRoot();
        var chatPage = File.ReadAllText(Path.Combine(
            root,
            "src",
            "GnOuGo.Agent.Server",
            "Components",
            "Pages",
            "ChatPage.razor"));

        Assert.DoesNotContain("new ChatMessageDto(\"thinking\"", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-workflow-card", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-execution-panel", chatPage, StringComparison.Ordinal);
        Assert.True(
            chatPage.IndexOf("gnougo-workflow-card-wrap", StringComparison.Ordinal)
            < chatPage.IndexOf("gnougo-chat__bubble", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentAnimationClient_QueuesFastTelemetryAndResizesTheFullWidthScene()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Combine(root, "src", "GnOuGo.Agent.Server");
        var main = File.ReadAllText(Path.Combine(agentRoot, "ClientApp", "src", "main.ts"));
        var styles = File.ReadAllText(Path.Combine(agentRoot, "ClientApp", "src", "styles", "app.scss"));
        var chatPage = File.ReadAllText(Path.Combine(agentRoot, "Components", "Pages", "ChatPage.razor"));
        var project = File.ReadAllText(Path.Combine(agentRoot, "GnOuGo.Agent.Server.csproj"));
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "src",
            "GnOuGo.Assets.Animation",
            "Runtime",
            "gnougnou-workflow-animation-controller.ts"));

        Assert.Contains("controller.enqueueEvent(event)", main, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver(resize)", main, StringComparison.Ordinal);
        Assert.Contains("Promise<boolean>", main, StringComparison.Ordinal);
        Assert.Contains(".gnougo-workflow-card__stage", styles, StringComparison.Ordinal);
        Assert.Contains("height: auto;", styles, StringComparison.Ordinal);
        Assert.Contains("max-width: none;", styles, StringComparison.Ordinal);
        Assert.Contains("InvokeAsync<bool>", chatPage, StringComparison.Ordinal);
        Assert.Contains("_animationInteropGate", chatPage, StringComparison.Ordinal);
        Assert.Contains("PendingUpdates.TryPeek", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("CollapseExecutionLaterAsync", chatPage, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"Build;PrepareForPublish\"", project, StringComparison.Ordinal);
        Assert.Contains("enqueueEvent(event: WorkflowSimulationEvent)", runtime, StringComparison.Ordinal);
        Assert.Contains("persistentActionTimers", runtime, StringComparison.Ordinal);
        Assert.Contains("data-animation-last-event", runtime, StringComparison.Ordinal);
        Assert.Contains("durationMs < 30_000", runtime, StringComparison.Ordinal);
    }

    private static JsonNode Json(SmartFlowEvent evt)
    {
        Assert.False(string.IsNullOrWhiteSpace(evt.Text));
        return JsonNode.Parse(evt.Text!)!;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GnOuGo.Agent.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
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
