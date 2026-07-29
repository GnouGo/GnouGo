using System.Xml.Linq;
using GnOuGo.Assets.Animation.Preview;
using Xunit;

namespace GnOuGo.Assets.Animation.Tests;

public sealed class LiveWorkflowAnimationTests
{
    [Fact]
    public void BuildLive_ProducesSceneWithoutSyntheticSchedule()
    {
        var validation = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: approve, type: human.input }
            """);

        var plan = GnouGnouAnimationPlanner.BuildLive(validation, new GnouGnouAnimationOptions { Seed = 7 });

        Assert.NotEmpty(plan.Nodes);
        Assert.Empty(plan.Events);
        Assert.Equal(0, plan.DurationMs);
        _ = XDocument.Parse(GnouGnouAnimationSvgRenderer.Render(plan).Svg);
    }

    [Fact]
    public void BuildLive_RendersDedicatedOrchestrationStations()
    {
        var validation = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: generate, type: workflow.plan }
                  - { id: route, type: workflow.route }
                  - { id: run, type: workflow.execute }
            """);

        var plan = GnouGnouAnimationPlanner.BuildLive(
            validation,
            new GnouGnouAnimationOptions { Seed = 21 });
        var svg = GnouGnouAnimationSvgRenderer.Render(plan).Svg;

        Assert.Contains(plan.Stations, station =>
            station.StepId == "generate" && station.Kind == AnimationStationKind.Planning);
        Assert.Contains(plan.Stations, station =>
            station.StepId == "route" && station.Kind == AnimationStationKind.Mcp);
        Assert.Contains(plan.Stations, station =>
            station.StepId == "run" && station.Kind == AnimationStationKind.HandoffDesk);
        Assert.True(svg.Split("class=\"workflow-roundabout\"", StringSplitOptions.None).Length - 1 >= 3);
        Assert.Contains(">✦</text>", svg, StringComparison.Ordinal);
        Assert.Contains(">↗</text>", svg, StringComparison.Ordinal);
        Assert.Contains(">⇄</text>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void HumanInput_IsVisibleAndProducesWaitingAndResumeEvents()
    {
        var validation = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: approve
                    type: human.input
            """);
        var plan = GnouGnouAnimationPlanner.BuildLive(
            validation,
            new GnouGnouAnimationOptions { Seed = 12 });
        var station = Assert.Single(plan.Stations, item => item.StepId == "approve");
        Assert.Equal(AnimationStationKind.Human, station.Kind);

        var session = new WorkflowLiveAnimationSession(plan);
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "run-main",
            WorkflowName = "main"
        });
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = "run-main",
            StepOccurrenceId = "approve-1",
            StepId = "approve",
            StepType = "human.input"
        });
        var waiting = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.HumanInputWaiting,
            WorkflowInstanceId = "run-main",
            StepOccurrenceId = "approve-1",
            StepId = "approve",
            StepType = "human.input"
        });
        var resumed = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.HumanInputResumed,
            WorkflowInstanceId = "run-main",
            StepOccurrenceId = "approve-1",
            StepId = "approve",
            StepType = "human.input"
        });

        Assert.Contains(waiting, update => update.Event?.Type == SimulationEventTypes.HumanInputWaiting);
        Assert.Contains(waiting, update => update.Event?.Type == SimulationEventTypes.ActorWaiting);
        Assert.Contains(resumed, update => update.Event?.Type == SimulationEventTypes.HumanInputResumed);
    }

    [Fact]
    public void LiveSession_UsesStableParcelAcrossChildHandoffAndCompletion()
    {
        var validation = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: delegate
                    type: workflow.call
                    input:
                      ref: { kind: local, name: child }
              child:
                steps:
                  - { id: research, type: llm.call }
            """);
        var plan = GnouGnouAnimationPlanner.BuildLive(
            validation,
            new GnouGnouAnimationOptions { Seed = 4 });
        var session = new WorkflowLiveAnimationSession(plan);

        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "root",
            WorkflowName = "main"
        });
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = "root",
            StepOccurrenceId = "delegate-1",
            StepId = "delegate",
            StepType = "workflow.call"
        });
        var childStart = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "child-1",
            ParentWorkflowInstanceId = "root",
            CallerStepOccurrenceId = "delegate-1",
            WorkflowName = "child"
        });
        var childEnd = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowCompleted,
            WorkflowInstanceId = "child-1",
            ParentWorkflowInstanceId = "root",
            WorkflowName = "child",
            Status = SimulationStatus.Succeeded
        });

        var handoffs = childStart.Concat(childEnd)
            .Select(update => update.Event)
            .Where(item => item?.Type == SimulationEventTypes.TaskHandedOff)
            .ToArray();
        Assert.Equal(2, handoffs.Length);
        Assert.All(handoffs, item => Assert.Equal("task-root", item!.TaskId));

        var masterActorId = plan.Lanes.Single(lane => lane.WorkflowName == "main").ActorId;
        var childActorId = plan.Lanes.Single(lane => lane.WorkflowName == "child").ActorId;
        var outbound = childStart
            .Select(update => update.Event)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        var masterDeparture = Assert.Single(outbound, item =>
            item.Type == SimulationEventTypes.ActorMoved
            && item.ActorId == masterActorId);
        var specialistArrival = Assert.Single(outbound, item =>
            item.Type == SimulationEventTypes.ActorSpawned
            && item.ActorId == childActorId);
        var outboundHandoff = Assert.Single(outbound, item =>
            item.Type == SimulationEventTypes.TaskHandedOff);
        var specialistEntry = Assert.Single(outbound, item =>
            item.Type == SimulationEventTypes.ActorMoved
            && item.ActorId == childActorId);
        Assert.Equal("task-root", masterDeparture.TaskId);
        Assert.Equal("task-root", specialistEntry.TaskId);
        Assert.True(masterDeparture.Sequence < specialistArrival.Sequence);
        Assert.True(specialistArrival.Sequence < outboundHandoff.Sequence);
        Assert.True(outboundHandoff.Sequence < specialistEntry.Sequence);

        var returning = childEnd
            .Select(update => update.Event)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        var returnHandoff = Assert.Single(returning, item =>
            item.Type == SimulationEventTypes.TaskHandedOff);
        var specialistReturn = Assert.Single(returning, item =>
            item.Type == SimulationEventTypes.ActorMoved
            && item.ActorId == childActorId);
        var masterReturn = Assert.Single(returning, item =>
            item.Type == SimulationEventTypes.ActorMoved
            && item.ActorId == masterActorId);
        Assert.True(specialistReturn.Sequence < returnHandoff.Sequence);
        Assert.True(returnHandoff.Sequence < masterReturn.Sequence);
    }

    [Fact]
    public void LiveSession_DiscoversRuntimeWorkflowWithoutLeakingSource()
    {
        var root = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: route, type: workflow.route }
            """);
        var plan = GnouGnouAnimationPlanner.BuildLive(root, new GnouGnouAnimationOptions { Seed = 99 });
        var session = new WorkflowLiveAnimationSession(plan);
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "root",
            WorkflowName = "main"
        });
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = "root",
            StepOccurrenceId = "route-1",
            StepId = "route",
            StepType = "workflow.route"
        });

        const string childSource = """
            version: 1
            entrypoint: selected
            workflows:
              selected:
                steps:
                  - { id: ask-user, type: human.input }
                  - { id: call-model, type: llm.call }
            """;
        var updates = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "dynamic-child",
            ParentWorkflowInstanceId = "root",
            CallerStepOccurrenceId = "route-1",
            WorkflowName = "selected",
            SourceText = childSource
        });

        var patch = Assert.Single(updates, update => update.ScenePatch is not null).ScenePatch!;
        var discovered = Assert.Single(updates, update =>
            update.Event?.Type == SimulationEventTypes.WorkflowDiscovered).Event!;
        var masterDeparture = Assert.Single(updates, update =>
            update.Event is
            {
                Type: SimulationEventTypes.ActorMoved,
                TaskId: "task-root"
            }
            && update.Event.ActorId == plan.Lanes.Single(lane => lane.IsEntrypoint).ActorId).Event!;
        var routingNode = plan.Nodes.Single(node => node.Id == discovered.NodeId);
        Assert.Contains(patch.Stations, station => station.StepId == "ask-user" && station.Kind == AnimationStationKind.Human);
        Assert.Contains(patch.Stations, station => station.StepId == "call-model");
        Assert.Equal("route", discovered.StepId);
        Assert.Equal("workflow.route", discovered.StepType);
        Assert.NotNull(discovered.StationId);
        Assert.Equal(routingNode.Id, masterDeparture.NodeId);
        Assert.Equal(routingNode.Position.X, masterDeparture.X);
        Assert.Equal(routingNode.Position.Y, masterDeparture.Y);
        Assert.Contains("router selects", discovered.Message, StringComparison.Ordinal);
        Assert.Contains("data-live-actor=\"true\"", patch.SvgFragment, StringComparison.Ordinal);
        Assert.Contains($"data-lane-id=\"{patch.Lanes[0].Id}\"", patch.SvgFragment, StringComparison.Ordinal);
        Assert.Equal(plan.Lanes.Single(lane => lane.IsEntrypoint).X, patch.Lanes[0].X);
        Assert.Equal(plan.Bounds.Width, patch.Bounds.Width);
        Assert.Contains(
            $"class=\"workflow-station\" data-step-id=\"ask-user\" data-step-type=\"human.input\" data-station-kind=\"human\" data-workflow-instance-id=\"dynamic-child\" data-lane-id=\"{patch.Lanes[0].Id}\"",
            patch.SvgFragment,
            StringComparison.Ordinal);
        Assert.DoesNotContain("version: 1", patch.SvgFragment, StringComparison.Ordinal);
        _ = XDocument.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\">{patch.SvgFragment}</svg>");
    }

    [Fact]
    public void LiveSession_UsesCompactLeafStationsWhenGeneratedWorkflowHasOnlyShortSteps()
    {
        var root = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: run, type: workflow.execute }
            """);
        var session = new WorkflowLiveAnimationSession(
            GnouGnouAnimationPlanner.BuildLive(root, new GnouGnouAnimationOptions { Seed = 31 }));
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "root",
            WorkflowName = "main"
        });
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = "root",
            StepOccurrenceId = "run-1",
            StepId = "run",
            StepType = "workflow.execute"
        });

        var updates = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "generated-child",
            ParentWorkflowInstanceId = "root",
            CallerStepOccurrenceId = "run-1",
            WorkflowName = "generated",
            SourceText = """
                version: 1
                entrypoint: generated
                workflows:
                  generated:
                    steps:
                      - { id: prepare, type: set }
                      - { id: format, type: template.render }
                """
        });

        var patch = Assert.Single(updates, update => update.ScenePatch is not null).ScenePatch!;
        var discovered = Assert.Single(updates, update =>
            update.Event?.Type == SimulationEventTypes.WorkflowDiscovered).Event!;
        Assert.Contains(patch.Stations, station => station.StepId == "prepare");
        Assert.Contains(patch.Stations, station => station.StepId == "format");
        Assert.DoesNotContain(patch.Stations, station => station.StepId == "dynamic-work");
        Assert.Equal("workflow.execute", discovered.StepType);
        Assert.Contains("generated blueprint", discovered.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveSession_AppendsRuntimeStepPatchWhenWorkflowSourceIsUnavailable()
    {
        var root = WorkflowPreviewValidator.ParseAndValidate("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - { id: route, type: workflow.route }
            """);
        var session = new WorkflowLiveAnimationSession(
            GnouGnouAnimationPlanner.BuildLive(root, new GnouGnouAnimationOptions { Seed = 41 }));
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "root",
            WorkflowName = "main"
        });
        session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.WorkflowStarted,
            WorkflowInstanceId = "runtime-child",
            ParentWorkflowInstanceId = "root",
            WorkflowName = "runtime-only"
        });
        var first = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = "runtime-child",
            StepOccurrenceId = "set-1",
            StepId = "prepare",
            StepType = "set"
        });
        var second = session.Apply(new AnimationExecutionSignal
        {
            Kind = AnimationExecutionSignalKind.StepStarted,
            WorkflowInstanceId = "runtime-child",
            StepOccurrenceId = "template-1",
            StepId = "format",
            StepType = "template.render"
        });

        Assert.DoesNotContain(first, update => update.ScenePatch is not null);
        var patch = Assert.Single(second, update => update.ScenePatch is not null).ScenePatch!;
        var stepStarted = Assert.Single(second, update =>
            update.Event?.Type == SimulationEventTypes.StepStarted).Event!;
        Assert.Contains(patch.Nodes, node => node.StepId == "format");
        Assert.Equal(patch.Nodes[0].Id, stepStarted.NodeId);
        Assert.Equal(patch.Stations[0].Id, stepStarted.StationId);
        _ = XDocument.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\">{patch.SvgFragment}</svg>");
    }
}
