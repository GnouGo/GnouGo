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
            WorkflowName = "selected",
            SourceText = childSource
        });

        var patch = Assert.Single(updates, update => update.ScenePatch is not null).ScenePatch!;
        Assert.Contains(patch.Stations, station => station.StepId == "ask-user" && station.Kind == AnimationStationKind.Human);
        Assert.Contains(patch.Stations, station => station.StepId == "call-model");
        Assert.DoesNotContain("version: 1", patch.SvgFragment, StringComparison.Ordinal);
        _ = XDocument.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\">{patch.SvgFragment}</svg>");
    }
}
