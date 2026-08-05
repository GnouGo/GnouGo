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
    public void AgentStreamingTelemetry_RoutedHumanInputEmitsWaitingAndResumeAnimationEvents()
    {
        const string yaml = """
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: route
                    type: workflow.route
            """;
        var events = new List<SmartFlowEvent>();
        var bridge = AgentWorkflowAnimationBridge.Create(
            yaml,
            "main",
            "11223344556677889900aabbccddeeff",
            events.Add,
            out _);
        var telemetry = new AgentStreamingTelemetry(events.Add, bridge);
        var workflow = telemetry.WorkflowStart(new WorkflowTelemetryInfo
        {
            WorkflowName = "main",
            SourceText = yaml
        });
        var route = telemetry.StepStart(workflow, new StepTelemetryInfo
        {
            StepId = "route",
            StepType = "workflow.route"
        });

        route.AddEvent("gnougo-flow.step.waiting_for_human", [
            new("gnougo-flow.human.request", """{"run_id":"run","step_id":"route:inputs:child","prompt":"Repository?","mode":"form"}""")
        ]);
        route.AddEvent("gnougo-flow.step.human_input_resumed", [
            new("gnougo-flow.human.run_id", "run"),
            new("gnougo-flow.human.step_id", "route:inputs:child")
        ]);

        var animationEvents = events
            .Where(item => item.Type == "animation.event")
            .Select(item => item.Animation?.Event)
            .OfType<SimulationEvent>()
            .ToArray();
        Assert.Contains(animationEvents, item => item.Type == SimulationEventTypes.HumanInputWaiting);
        Assert.Contains(animationEvents, item => item.Type == SimulationEventTypes.HumanInputResumed);
        var waitingIndex = events.FindIndex(item =>
            item.Animation?.Event?.Type == SimulationEventTypes.HumanInputWaiting);
        var requestIndex = events.FindIndex(item => item.Type == "human_input_request");
        Assert.True(waitingIndex >= 0 && requestIndex > waitingIndex);
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
            item.Type == SimulationEventTypes.WorkflowDiscovered
            && item.WorkflowName == "fallback_general"
            && item.StepId == "route"
            && item.StepType == "workflow.route"
            && item.StationId is not null);
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
    public void ChatPage_KeepsThinkingOutOfHistoryAndOwnsAnimationPerAssistantResponse()
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
        Assert.Contains("gnougo-chat__response-animation", chatPage, StringComparison.Ordinal);
        Assert.Contains("GnOuGo animation for this response", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("gnougo-sidebar__workflow", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SidebarExecution", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__response-actions", chatPage, StringComparison.Ordinal);
        Assert.Contains("CopyMessageAsync(msg.Content)", chatPage, StringComparison.Ordinal);
        Assert.Contains("PlainTextContent Class=\"gnougo-chat__bubble-text gnougo-chat__bubble-text--user\"", chatPage, StringComparison.Ordinal);
        Assert.Contains("ChatComposerText.PreserveForSubmission(_model.Prompt)", chatPage, StringComparison.Ordinal);
        Assert.Contains("data-gnougo-autogrow=\"true\"", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("var prompt = _model.Prompt.Trim()", chatPage, StringComparison.Ordinal);
        Assert.Contains("execution is null", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-sidebar__mascot", chatPage, StringComparison.Ordinal);
        Assert.Contains("SidebarConversationGrouping.Group", chatPage, StringComparison.Ordinal);
        Assert.Contains("GnouGnouBearAnimation.Idle", chatPage, StringComparison.Ordinal);
        Assert.Contains("SvgIdPrefix = \"agent-sidebar-gnougo\"", chatPage, StringComparison.Ordinal);
        Assert.Contains("<div class=\"gnougo-sidebar__title\">GnOuGo</div>", chatPage, StringComparison.Ordinal);
        Assert.Contains("Simple. Safe. Transparent.", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__product-mark", chatPage, StringComparison.Ordinal);
        Assert.Contains(">G</span>", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__agent-menu", chatPage, StringComparison.Ordinal);
        Assert.Contains("SelectAgentAsync(agent)", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__more-trigger", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("chatAgentSelect", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("gnougo-chat__agent-select", chatPage, StringComparison.Ordinal);
        Assert.Contains("Open workflow activity", chatPage, StringComparison.Ordinal);
        Assert.Contains("Show GnOuGo animation", chatPage, StringComparison.Ordinal);
        Assert.Contains("<span>Traces</span>", chatPage, StringComparison.Ordinal);
        Assert.Contains("<span>Animation</span>", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("GnOuGo team execution", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("visual node", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Live telemetry ·", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-workflow-card__header", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("gnougo-workflow-card__stage-toolbar", chatPage, StringComparison.Ordinal);
        Assert.True(
            chatPage.IndexOf("OpenTraceSidebar(msg)", StringComparison.Ordinal)
            < chatPage.IndexOf("OpenExecutionSidebar(execution)", StringComparison.Ordinal));
        Assert.True(
            chatPage.IndexOf("gnougo-chat__response-animation", StringComparison.Ordinal)
            < chatPage.IndexOf("gnougo-chat__response-actions", StringComparison.Ordinal));
    }

    [Fact]
    public void ChatPage_RendersWorkflowExceptionsInsideTheirAssistantResponse()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Combine(root, "src", "GnOuGo.Agent.Server");
        var chatPage = File.ReadAllText(Path.Combine(agentRoot, "Components", "Pages", "ChatPage.razor"));
        var styles = File.ReadAllText(Path.Combine(agentRoot, "ClientApp", "src", "styles", "app.scss"));

        Assert.Contains("var responseError = isUser ? null : GetWorkflowResponseError(msg);", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__response-error", chatPage, StringComparison.Ordinal);
        Assert.Contains("SetWorkflowResponseError(assistantMsg, errText);", chatPage, StringComparison.Ordinal);
        Assert.Contains("SetWorkflowResponseError(assistantMsg, ex.Message);", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("_error = errText;", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("_error = ex.Message;", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__response-animation", chatPage, StringComparison.Ordinal);
        Assert.Contains("MarkExecutionFailed(correlationId, errText);", chatPage, StringComparison.Ordinal);
        Assert.Contains("SimulationEventTypes.SimulationCompleted", chatPage, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__response-error {", styles, StringComparison.Ordinal);
        Assert.Contains("white-space: pre-wrap;", styles, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentAnimationClient_QueuesTelemetryAndFollowsInsideScrollableMessagePanel()
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
        Assert.Contains("host: HTMLElement", main, StringComparison.Ordinal);
        Assert.Contains("function mountedWorkflowAnimationHandle(hostId: string)", main, StringComparison.Ordinal);
        Assert.Contains("currentHost === handle.host && handle.host.isConnected", main, StringComparison.Ordinal);
        Assert.Contains("const handle = mountedWorkflowAnimationHandle(hostId);", main, StringComparison.Ordinal);
        Assert.Contains("if (!host.isConnected || el(hostId) !== host) return;", main, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver(resize)", main, StringComparison.Ordinal);
        Assert.Contains("Promise<boolean>", main, StringComparison.Ordinal);
        Assert.Contains("allowDocumentFocusScroll: false", main, StringComparison.Ordinal);
        Assert.Contains("cameraMode: 'scroll'", main, StringComparison.Ordinal);
        Assert.Contains("controller.focusEvent(event)", main, StringComparison.Ordinal);
        Assert.Contains("follow: boolean", main, StringComparison.Ordinal);
        Assert.Contains("host.dataset.follow = 'true'", main, StringComparison.Ordinal);
        Assert.Contains("shouldFollowPortalTransfer: () => handle.follow", main, StringComparison.Ordinal);
        Assert.Contains("if (handle.follow) controller.focusEvent(event)", main, StringComparison.Ordinal);
        Assert.Contains("svg.dataset.sceneWidth = String(sceneWidth)", main, StringComparison.Ordinal);
        Assert.Contains("const readableWidth = Math.min(logicalWidth, Math.max(640, availableWidth * 1.8))", main, StringComparison.Ordinal);
        Assert.Contains("setFollow: (hostId: string, follow: boolean)", main, StringComparison.Ordinal);
        Assert.Contains("fadeOut: async (hostId: string, durationMs = 360)", main, StringComparison.Ordinal);
        Assert.Contains("gnougo-workflow-card__stage--leaving", main, StringComparison.Ordinal);
        Assert.Contains("copyText,", main, StringComparison.Ordinal);
        Assert.Contains(".gnougo-workflow-card__stage", styles, StringComparison.Ordinal);
        Assert.Contains("height: clamp(340px, 52dvh, 620px);", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: min(620px, calc(100dvh - 220px));", styles, StringComparison.Ordinal);
        Assert.Contains("overflow: scroll;", styles, StringComparison.Ordinal);
        Assert.Contains("scrollbar-gutter: stable both-edges;", styles, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--gnougo-border);", styles, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--gnougo-radius);", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes gnougo-workflow-stage-enter", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-workflow-card__stage--leaving", styles, StringComparison.Ordinal);
        Assert.Contains("max-width: none;", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-sidebar__mascot", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__product-mark", styles, StringComparison.Ordinal);
        Assert.Contains("color: #0057ff;", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__agent-menu", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__bubble-text--user", styles, StringComparison.Ordinal);
        Assert.Contains("white-space: pre-wrap;", styles, StringComparison.Ordinal);
        Assert.Contains("field-sizing: content;", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__response-animation", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-workflow-card__header", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".gnougo-workflow-card__stage-toolbar", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Sidebar (blue", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "background: linear-gradient(180deg, var(--gnougo-accent)",
            styles,
            StringComparison.Ordinal);
        Assert.Contains("max-width: 1160px;", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__response-actions", styles, StringComparison.Ordinal);
        Assert.Contains("InvokeAsync<bool>", chatPage, StringComparison.Ordinal);
        Assert.Contains("data-follow=\"true\"", chatPage, StringComparison.Ordinal);
        Assert.Contains("_animationInteropGate", chatPage, StringComparison.Ordinal);
        Assert.Contains("var executionSnapshot = GetActiveExecutions()", chatPage, StringComparison.Ordinal);
        Assert.Contains("RecordAnimationUpdate(execution", chatPage, StringComparison.Ordinal);
        Assert.Contains("ResetExecutionForReplay(execution)", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("_sidebarExecutionCorrelationId", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("FadeOutSidebarAnimationAsync()", chatPage, StringComparison.Ordinal);
        Assert.Contains("var freshExecution = new ChatExecutionModel", chatPage, StringComparison.Ordinal);
        Assert.Contains("Prepared = prepared;", chatPage, StringComparison.Ordinal);
        Assert.Contains("public AnimationPreparedPayload? Prepared", chatPage, StringComparison.Ordinal);
        Assert.True(
            chatPage.IndexOf("var freshExecution = new ChatExecutionModel", StringComparison.Ordinal)
            < chatPage.IndexOf("SmartFlow.ExecuteAsync(", StringComparison.Ordinal));
        Assert.DoesNotContain("_animationScrollCorrelationId", chatPage, StringComparison.Ordinal);
        Assert.Contains("PendingUpdates.TryPeek", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("CollapseExecutionLaterAsync", chatPage, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"Build;PrepareForPublish\"", project, StringComparison.Ordinal);
        Assert.Contains("enqueueEvent(event: WorkflowSimulationEvent)", runtime, StringComparison.Ordinal);
        Assert.Contains("if (event.type === 'human_input.waiting')", runtime, StringComparison.Ordinal);
        Assert.Contains("private synchronizeHumanInputWaiting()", runtime, StringComparison.Ordinal);
        Assert.Contains("private settleHumanInputActor(event: WorkflowSimulationEvent)", runtime, StringComparison.Ordinal);
        Assert.Contains("persistentActionTimers", runtime, StringComparison.Ordinal);
        Assert.Contains("data-animation-last-event", runtime, StringComparison.Ordinal);
        Assert.Contains("durationMs < 30_000", runtime, StringComparison.Ordinal);
        Assert.Contains("private animateCamera(", runtime, StringComparison.Ordinal);
        Assert.Contains("private animateHumanInputDelivery(event: WorkflowSimulationEvent)", runtime, StringComparison.Ordinal);
        Assert.Contains("data-animation-human-delivery", runtime, StringComparison.Ordinal);
        Assert.Contains("human-input-delivery-", runtime, StringComparison.Ordinal);
        Assert.Contains("event.type === 'human_input.resumed'", runtime, StringComparison.Ordinal);
        Assert.Contains("event.stepType?.toLowerCase().startsWith('human.')", runtime, StringComparison.Ordinal);
        Assert.Contains("function isFailedStatus(status?: string)", runtime, StringComparison.Ordinal);
        Assert.Contains("target.x - routeEnd.x", runtime, StringComparison.Ordinal);
        Assert.Contains("from.x - routeStart.x", runtime, StringComparison.Ordinal);
        Assert.Contains("const LIVE_MOVEMENT_CAMERA_LEAD = .35", runtime, StringComparison.Ordinal);
        Assert.Contains("this.readPosition(event.actorId)", runtime, StringComparison.Ordinal);
        Assert.Contains("LIVE_MOVEMENT_CAMERA_LEAD", runtime, StringComparison.Ordinal);

        var resumedStart = runtime.IndexOf("case 'human_input.resumed':", StringComparison.Ordinal);
        var resumedEnd = runtime.IndexOf("case 'actor.cloned':", resumedStart, StringComparison.Ordinal);
        Assert.True(resumedStart >= 0 && resumedEnd > resumedStart);
        Assert.DoesNotContain(
            "activateSceneForActor",
            runtime[resumedStart..resumedEnd],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "characters.play",
            runtime[resumedStart..resumedEnd],
            StringComparison.Ordinal);
        Assert.Contains(
            "this.characters.stop(event.actorId, false)",
            runtime[resumedStart..resumedEnd],
            StringComparison.Ordinal);
        Assert.Contains("const isHumanInputStep", runtime, StringComparison.Ordinal);
        Assert.Contains("if (!isHumanInputStep) this.activateSceneForActor", runtime, StringComparison.Ordinal);
        Assert.Contains("else if (!isHumanInputStep)", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("isHumanInputStep ? 'communicate'", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPage_SnapshotsMutableCollectionsBeforeAwaitingAnimationInterop()
    {
        var root = FindRepositoryRoot();
        var chatPage = File.ReadAllText(Path.Combine(
            root,
            "src",
            "GnOuGo.Agent.Server",
            "Components",
            "Pages",
            "ChatPage.razor"));

        Assert.Contains("private IReadOnlyList<ChatExecutionModel> GetActiveExecutions()", chatPage, StringComparison.Ordinal);
        Assert.Contains("var messageSnapshot = active.Messages.ToArray();", chatPage, StringComparison.Ordinal);
        Assert.Contains("foreach (var message in messageSnapshot)", chatPage, StringComparison.Ordinal);
        Assert.Contains("var executionSnapshot = GetActiveExecutions()", chatPage, StringComparison.Ordinal);
        Assert.Contains(".Where(static item => item.IsExpanded && item.Prepared is not null)", chatPage, StringComparison.Ordinal);
        Assert.Contains(".ToArray();\n            foreach (var execution in executionSnapshot)", chatPage, StringComparison.Ordinal);
        Assert.Contains("var executionSnapshot = _executions.Values.ToArray();", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("private IEnumerable<ChatExecutionModel> GetActiveExecutions()", chatPage, StringComparison.Ordinal);

        var snapshotIndex = chatPage.IndexOf("var executionSnapshot = GetActiveExecutions()", StringComparison.Ordinal);
        var mountAwaitIndex = chatPage.IndexOf("var mounted = await Js.InvokeAsync<bool>", snapshotIndex, StringComparison.Ordinal);
        Assert.True(snapshotIndex >= 0 && mountAwaitIndex > snapshotIndex);
    }

    [Fact]
    public void ChatPage_ShowsTypingDotsUntilTheFinalResponseStarts()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Combine(root, "src", "GnOuGo.Agent.Server");
        var chatPage = File.ReadAllText(Path.Combine(agentRoot, "Components", "Pages", "ChatPage.razor"));
        var styles = File.ReadAllText(Path.Combine(agentRoot, "ClientApp", "src", "styles", "app.scss"));

        Assert.Contains("_streamingAssistantMessageId = assistantMsg.MessageId;", chatPage, StringComparison.Ordinal);
        Assert.Contains("_finalResponseStartedMessageId = assistantMsg.MessageId;", chatPage, StringComparison.Ordinal);
        Assert.Contains("var isTypingResponse = !isUser", chatPage, StringComparison.Ordinal);
        Assert.Contains("msg.MessageId,\n                                                   _finalResponseStartedMessageId", chatPage, StringComparison.Ordinal);
        Assert.Contains("GetExecution(correlationId)?.Prepared is null", chatPage, StringComparison.Ordinal);
        Assert.Contains("RecordPreliminaryResponse(correlationId, evt.Text);", chatPage, StringComparison.Ordinal);
        Assert.Contains("public List<string> PreliminaryResponses", chatPage, StringComparison.Ordinal);
        Assert.Contains("public List<(string Question, string Answer)> HumanInputSummaries", chatPage, StringComparison.Ordinal);
        Assert.Contains("execution.HumanInputSummaries.Add((question, answer));", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__response-progress", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__human-summary", chatPage, StringComparison.Ordinal);
        Assert.Contains("gnougo-chat__typing-indicator", chatPage, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", chatPage, StringComparison.Ordinal);
        Assert.Contains("_streamingAssistantMessageId = null;", chatPage, StringComparison.Ordinal);
        Assert.Contains("_finalResponseStartedMessageId = null;", chatPage, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__message--typing {", styles, StringComparison.Ordinal);
        Assert.Contains("justify-content: flex-start;", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__response-progress {", styles, StringComparison.Ordinal);
        Assert.Contains(".gnougo-chat__human-summary {", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes gnougo-chat-typing-dot", styles, StringComparison.Ordinal);
        Assert.Contains("background: #111;", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".gnougo-chat__typing-indicator {\n  display: inline-flex;\n  align-items: center;\n  justify-content: center;\n  gap: 5px;\n  min-height: 20px;\n  border:", styles, StringComparison.Ordinal);
        Assert.Contains("animation-delay: 140ms;", styles, StringComparison.Ordinal);
        Assert.Contains("animation-delay: 280ms;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void AskHuman_RenderingDoesNotBlockAnimationInteropOrRepeatContextFormatting()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Combine(root, "src", "GnOuGo.Agent.Server");
        var chatPage = File.ReadAllText(Path.Combine(agentRoot, "Components", "Pages", "ChatPage.razor"));
        var main = File.ReadAllText(Path.Combine(agentRoot, "ClientApp", "src", "main.ts"));

        Assert.Contains("ContextMarkdown = HumanInputContextMarkdownFormatter.Format(context)", chatPage, StringComparison.Ordinal);
        Assert.Contains("Content=\"@_pendingHumanInput.ContextMarkdown\"", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"@FormatHumanInputContextAsMarkdown", chatPage, StringComparison.Ordinal);
        Assert.Contains("_pendingHumanInput.Mode.Equals(HumanInputContract.ModeConfirm", chatPage, StringComparison.Ordinal);
        Assert.Contains("HumanInputContract.TryReadConfirmation(", chatPage, StringComparison.Ordinal);
        Assert.Contains("responseValue = JsonValue.Create(confirmed);", chatPage, StringComparison.Ordinal);
        var styles = File.ReadAllText(Path.Combine(agentRoot, "ClientApp", "src", "styles", "app.scss"));
        Assert.Contains(".gnougo-workflow-hitl {", styles, StringComparison.Ordinal);
        Assert.Contains("justify-content: center;", styles, StringComparison.Ordinal);
        Assert.Contains("width: min(860px, 100%);", styles, StringComparison.Ordinal);
        Assert.Contains("border-radius: 18px;", styles, StringComparison.Ordinal);
        Assert.True(
            chatPage.IndexOf("await FlushAnimationInteropAsync();", StringComparison.Ordinal)
            < chatPage.IndexOf("GnOuGo.Agent.markdown.enhance", StringComparison.Ordinal));

        Assert.Contains("function scheduleMermaidRender(id: string): void", main, StringComparison.Ordinal);
        Assert.Contains("enhance: scheduleMermaidRender", main, StringComparison.Ordinal);
        Assert.Contains("MAX_MERMAID_SOURCE_LENGTH", main, StringComparison.Ordinal);
        Assert.DoesNotContain("enhance: renderMermaid", main, StringComparison.Ordinal);
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
