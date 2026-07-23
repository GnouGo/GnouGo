using System.Text.Json.Nodes;
using System.Xml.Linq;
using GnOuGo.Assets.Animation.Preview;
using Xunit;

namespace GnOuGo.Assets.Animation.Tests;

public sealed class AnimationPlannerAndRendererTests
{
    private const string TeamYaml = """
        version: 1
        name: Autonomous team
        entrypoint: main
        workflows:
          main:
            steps:
              - id: prepare
                type: set
              - id: matrix
                type: parallel
                branches:
                  - name: ideas
                    steps:
                      - id: think
                        type: llm.call
                  - name: sources
                    steps:
                      - id: contact
                        type: mcp.call
              - id: delegate
                type: workflow.call
                input:
                  ref:
                    kind: local
                    name: helper
              - id: publish
                type: emit
          helper:
            steps:
              - id: draft
                type: llm.call
        """;

    [Fact]
    public void Build_IsDeterministicForSameSeed()
    {
        var validation = Valid(TeamYaml);

        var first = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions { Seed = 73 });
        var second = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions { Seed = 73 });

        Assert.Equal(first.Scene, second.Scene);
        Assert.Equal(first.Actors, second.Actors);
        Assert.Equal(first.Events, second.Events);
        Assert.Equal(
            GnouGnouAnimationSvgRenderer.Render(first).Svg,
            GnouGnouAnimationSvgRenderer.Render(second).Svg);
    }

    [Theory]
    [InlineData(AnimationSceneKind.Office, "scene-office")]
    [InlineData(AnimationSceneKind.Meadow, "scene-meadow")]
    [InlineData(AnimationSceneKind.Kitchen, "scene-kitchen")]
    public void Render_ProducesEachValidSemanticScene(AnimationSceneKind scene, string sceneId)
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions { Seed = 11, Scene = scene });
        var result = GnouGnouAnimationSvgRenderer.Render(plan);

        var xml = XDocument.Parse(result.Svg);
        Assert.Equal("svg", xml.Root?.Name.LocalName);
        Assert.True(result.Width >= 1600);
        Assert.True(result.Height >= 900);
        Assert.True(result.Width >= plan.Bounds.Width);
        Assert.True(result.Height >= plan.Bounds.Height);
        Assert.Equal($"{result.Width}", xml.Root?.Attribute("width")?.Value);
        Assert.Equal($"0 0 {result.Width} {result.Height}", xml.Root?.Attribute("viewBox")?.Value);
        Assert.NotNull(xml.Descendants().FirstOrDefault(element => element.Attribute("id")?.Value == sceneId));
        Assert.DoesNotContain(
            xml.Descendants().Attributes("id").GroupBy(attribute => attribute.Value),
            group => group.Count() > 1);
        Assert.DoesNotContain("actor-halo", result.Svg, StringComparison.Ordinal);
        Assert.DoesNotContain("ground-shadow", result.Svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EscapesLabelsAndMarksOnlyMasterLineageAsBearded()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions { Seed = 4, Scene = AnimationSceneKind.Office });
        var rendered = GnouGnouAnimationSvgRenderer.Render(plan, new GnouGnouAnimationRenderOptions
        {
            Title = "Team <safe> & \"visible\"",
            Description = "Description </desc><script>alert(1)</script>"
        });
        var xml = XDocument.Parse(rendered.Svg);
        XNamespace svg = "http://www.w3.org/2000/svg";

        Assert.Equal("Team <safe> & \"visible\"", xml.Root?.Element(svg + "title")?.Value);
        Assert.DoesNotContain("</desc><script>", rendered.Svg, StringComparison.Ordinal);
        var master = xml.Descendants().Single(element => element.Attribute("id")?.Value == "actor-master");
        Assert.Equal("true", master.Attribute("data-bearded")?.Value);
        Assert.NotNull(master.Descendants().SingleOrDefault(element => element.Attribute("data-animation-rig")?.Value == "true"));
        foreach (var part in new[] { "head", "ear-left", "ear-right", "arm-left", "arm-right", "leg-left", "leg-right", "eye-left", "pupil-left", "mouth", "action-fx" })
            Assert.NotNull(master.Descendants().FirstOrDefault(element => element.Attribute("data-part")?.Value == part));
        Assert.NotNull(master.Descendants().FirstOrDefault(element => element.Attribute("data-part")?.Value == "beard"));
        Assert.All(
            xml.Descendants().Where(element => element.Attribute("data-actor-kind")?.Value == "worker"),
            element => Assert.Equal("false", element.Attribute("data-bearded")?.Value));
    }

    [Fact]
    public void Build_ModelsSkyTravelAndSuccessfulChildHandoffs()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions { Seed = 3 });

        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.TaskDropped && item.TaskId == "task-root");
        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.OutputSent && item.Y == -90 && item.Status == SimulationStatus.Succeeded);
        var handoffs = plan.Events.Where(item => item.Type == SimulationEventTypes.TaskHandedOff).ToArray();
        Assert.Contains(handoffs, item => item.ActorId == "actor-master" && item.TargetActorId?.Contains("helper", StringComparison.Ordinal) == true);
        Assert.Contains(handoffs, item => item.ActorId?.Contains("helper", StringComparison.Ordinal) == true && item.TargetActorId == "actor-master");
        Assert.All(handoffs, item => Assert.Equal("task-root", item.TaskId));
        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.ActorWaiting && item.ActorId == "actor-master");
        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.ActorMoved && item.ActorId == "actor-master"
            && item.NodeId == plan.Nodes.Single(node => node.Kind == AnimationFlowNodeKind.Delivery).Id);
        Assert.Equal(2, plan.Actors.Count(item => item.Kind != AnimationActorKind.Clone));
    }

    [Fact]
    public void Build_UsesOneEvolvingParcelAndOnlyLongRunningTaskDesks()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions
        {
            Seed = 31,
            Scene = AnimationSceneKind.Kitchen
        });
        var root = Assert.Single(plan.Tasks, item => item.Kind == "project-parcel");
        var visibleAtomicSteps = new[] { "think", "contact", "draft" };

        Assert.Equal("task-root", root.Id);
        Assert.All(
            plan.Events.Where(item =>
                item.Type is SimulationEventTypes.StepStarted or SimulationEventTypes.StepCompleted
                && item.StepType is not "parallel" and not "workflow.call"),
            item => Assert.StartsWith("task-", item.TaskId, StringComparison.Ordinal));
        Assert.Equal(
            visibleAtomicSteps.Order(),
            plan.Stations
                .Where(item => item.Kind == AnimationStationKind.KeyboardDesk)
                .Select(item => item.StepId!)
                .Order());
        Assert.DoesNotContain(plan.Stations, item => item.StepId is "prepare" or "publish");
        Assert.Contains(plan.Stations, item => item.StepId == "delegate" && item.Kind == AnimationStationKind.HandoffDesk);
        Assert.Contains(plan.Stations, item => item.Kind == AnimationStationKind.DeliveryDock);

        var svg = GnouGnouAnimationSvgRenderer.Render(plan).Svg;
        Assert.Contains("data-step-id=\"think\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-task-kind=\"project-parcel\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"desk-key\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"parcel-stamp\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"isometric-desk\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"laptop-screen\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"isometric-sign\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"route-surface\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"route-stone\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-route-path=\"true\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CollapsesShortStepsAndVariesVisibleLaptopDurations()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - { id: quick, type: set }
                  - { id: mcp-work, type: mcp.call }
                  - { id: llm-work, type: llm.call }
            """), new GnouGnouAnimationOptions { Seed = 41, WorkDurationMs = 1000 });

        var quick = plan.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "quick");
        var mcp = plan.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "mcp-work");
        var llm = plan.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "llm-work");

        Assert.True(quick.DurationMs < mcp.DurationMs);
        Assert.True(mcp.DurationMs < llm.DurationMs);
        Assert.Null(quick.StationId);
        Assert.NotNull(mcp.StationId);
        Assert.NotNull(llm.StationId);
        Assert.Contains("without a visual workstation", quick.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AllocatesTopDownLanesBranchesAndAlignedChildReturn()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions { Seed = 73 });
        var masterLane = plan.Lanes.Single(lane => lane.IsEntrypoint);
        var childLane = plan.Lanes.Single(lane => !lane.IsEntrypoint);
        var call = plan.Nodes.Single(node => node.WorkflowInstanceId == "workflow-master"
                                             && node.StepId == "delegate"
                                             && node.Kind == AnimationFlowNodeKind.WorkflowCall);
        var childStart = plan.Nodes.Single(node => node.WorkflowInstanceId == childLane.WorkflowInstanceId && node.Kind == AnimationFlowNodeKind.Start);
        var childFinish = plan.Nodes.Single(node => node.WorkflowInstanceId == childLane.WorkflowInstanceId && node.Kind == AnimationFlowNodeKind.Finish);
        var returned = plan.Nodes.Single(node => node.WorkflowInstanceId == "workflow-master" && node.Kind == AnimationFlowNodeKind.Return);
        var fork = plan.Nodes.Single(node => node.StepId == "matrix" && node.Kind == AnimationFlowNodeKind.Fork);
        var join = plan.Nodes.Single(node => node.StepId == "matrix" && node.Kind == AnimationFlowNodeKind.Join);
        var branchDesks = plan.Nodes.Where(node => node.StepId is "think" or "contact").ToArray();

        Assert.True(childLane.X > masterLane.X);
        Assert.Equal(call.Position.Y, childStart.Position.Y);
        Assert.True(returned.Position.Y > childFinish.Position.Y);
        Assert.Equal(2, branchDesks.Select(node => node.Position.X).Distinct().Count());
        Assert.All(branchDesks, node => Assert.True(node.Position.Y > fork.Position.Y));
        Assert.True(join.Position.Y > branchDesks.Max(node => node.Position.Y));
        Assert.All(
            plan.Edges.Where(edge => edge.Kind == AnimationFlowEdgeKind.Handoff),
            edge => Assert.True(plan.Nodes.Single(node => node.Id == edge.ToNodeId).Position.X
                                > plan.Nodes.Single(node => node.Id == edge.FromNodeId).Position.X));
        Assert.True(plan.Bounds.Width >= 1600);
        Assert.True(plan.Bounds.Height >= 900);
    }

    [Fact]
    public void Build_RendersEverySwitchRouteButMarksOnlySeededRouteSelected()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: choose
                    type: switch
                    expr: ${runtime.route}
                    cases:
                      - value: alpha
                        steps:
                          - id: alpha-work
                            type: llm.call
                      - value: beta
                        steps:
                          - id: beta-work
                            type: mcp.call
            """), new GnouGnouAnimationOptions { Seed = 91 });

        var routeNodes = plan.Nodes.Where(node => node.StepId is "alpha-work" or "beta-work").ToArray();
        Assert.Equal(2, routeNodes.Length);
        Assert.Single(routeNodes, node => node.IsSelected);
        Assert.Single(routeNodes, node => !node.IsSelected);
        Assert.Contains(plan.Edges, edge => !edge.IsSelected);
    }

    [Fact]
    public void Build_OverlapsNestedMatrixBranchesAndMergesEveryClone()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: outer
                    type: parallel
                    branches:
                      - name: nested
                        steps:
                          - id: inner
                            type: parallel
                            branches:
                              - name: one
                                steps:
                                  - id: inner-a
                                    type: llm.call
                              - name: two
                                steps:
                                  - id: inner-b
                                    type: mcp.call
                      - name: sibling
                        steps:
                          - id: outer-b
                            type: template.render
            """), new GnouGnouAnimationOptions { Seed = 12 });

        var outerCloneAt = plan.Events.First(item => item.Type == SimulationEventTypes.ActorCloned).OffsetMs;
        var nestedCloneAt = plan.Events.Where(item => item.Type == SimulationEventTypes.ActorCloned).Max(item => item.OffsetMs);
        var sibling = plan.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "outer-b");
        var innerA = plan.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "inner-a");
        var innerB = plan.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "inner-b");

        Assert.True(nestedCloneAt > outerCloneAt);
        Assert.Equal(innerA.OffsetMs, innerB.OffsetMs);
        Assert.True(sibling.OffsetMs < innerA.OffsetMs + innerA.DurationMs);
        Assert.Equal(
            plan.Events.Count(item => item.Type == SimulationEventTypes.ActorCloned),
            plan.Events.Count(item => item.Type == SimulationEventTypes.ActorMerged));
        Assert.Equal(1, plan.Actors.Count(item => item.Kind == AnimationActorKind.Master));
    }

    [Fact]
    public void Build_AllocatesChildLaneForWorkflowCallMadeByMatrixClone()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: matrix
                    type: parallel
                    branches:
                      - name: delegated
                        steps:
                          - id: delegate
                            type: workflow.call
                            input:
                              ref: { kind: local, name: helper }
                      - name: local
                        steps:
                          - id: local-work
                            type: mcp.call
              helper:
                steps:
                  - id: child-work
                    type: llm.call
            """), new GnouGnouAnimationOptions { Seed = 52 });

        var childLane = Assert.Single(plan.Lanes, lane => lane.WorkflowName == "helper");
        var childStart = plan.Nodes.Single(node => node.WorkflowInstanceId == childLane.WorkflowInstanceId
                                                  && node.Kind == AnimationFlowNodeKind.Start);
        var call = plan.Nodes.Single(node => node.WorkflowInstanceId == "workflow-master"
                                             && node.StepId == "delegate"
                                             && node.Kind == AnimationFlowNodeKind.WorkflowCall);
        var handoff = Assert.Single(plan.Events, item => item.Type == SimulationEventTypes.TaskHandedOff
                                                       && item.TargetActorId == childLane.ActorId);

        Assert.Equal(call.Position.Y, childStart.Position.Y);
        Assert.StartsWith("actor-clone-", handoff.ActorId, StringComparison.Ordinal);
        Assert.StartsWith("task-clone-", handoff.TaskId, StringComparison.Ordinal);
        Assert.Contains(plan.Edges, edge => edge.Kind == AnimationFlowEdgeKind.Handoff
                                           && edge.FromNodeId == call.Id
                                           && edge.ToNodeId == childStart.Id);
    }

    [Fact]
    public void Build_HonorsInputArraysAndCapsLoopsAtFive()
    {
        var validation = Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: repeat
                    type: loop.sequential
                    input:
                      items: ${data.inputs.items}
                    steps:
                      - id: repeated-work
                        type: mcp.call
            """);
        var plan = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions
        {
            Seed = 8,
            Inputs = JsonNode.Parse("{\"items\":[1,2,3,4,5,6,7]}")
        });

        Assert.Equal(5, plan.Events.Count(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "repeated-work"));
        Assert.Single(plan.Stations, item => item.StepId == "repeated-work");
        Assert.Contains(plan.Warnings, item => item.Code == "LOOP_TRUNCATED");
    }

    [Fact]
    public void Build_UsesStableSeededDecisions()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: route
                    type: switch
                    expr: ${runtime.route}
                    cases:
                      - value: alpha
                        steps:
                          - id: alpha-work
                            type: set
                      - value: beta
                        steps:
                          - id: beta-work
                            type: emit
            """;
        var validation = Valid(yaml);

        var first = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions { Seed = 91 });
        var second = GnouGnouAnimationPlanner.Build(validation, new GnouGnouAnimationOptions { Seed = 91 });

        Assert.Equal(
            first.Events.Single(item => item.Type == SimulationEventTypes.DecisionSimulated).BranchId,
            second.Events.Single(item => item.Type == SimulationEventTypes.DecisionSimulated).BranchId);
        Assert.Contains(first.Warnings, item => item.Code == "SIMULATED_SWITCH");
    }

    [Fact]
    public void Build_PropagatesChildFailureSkipsPendingWorkAndReturnsRedTask()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions
        {
            Seed = 44,
            FailAt = new SimulationFailureTarget("helper", "draft")
        });

        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.StepCompleted && item.StepId == "draft" && item.Status == SimulationStatus.Failed);
        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.TaskHandedOff && item.Status == SimulationStatus.Failed && item.TargetActorId == "actor-master");
        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.StepSkipped && item.StepId == "publish");
        Assert.Contains(plan.Events, item => item.Type == SimulationEventTypes.OutputSent && item.Status == SimulationStatus.Failed);
        Assert.Equal(SimulationStatus.Failed, plan.Events.Last(item => item.Type == SimulationEventTypes.SimulationCompleted).Status);
    }

    [Fact]
    public void Build_EnforcesStepActorAndCloneLimits()
    {
        var steps = Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: one
                    type: set
                  - id: two
                    type: emit
            """);
        var dynamicCall = Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: worker
                    type: workflow.call
                    input:
                      ref: { kind: local, name: helper }
              helper:
                steps:
                  - id: long-work
                    type: llm.call
            """);
        var fork = Valid("""
            version: 1
            workflows:
              main:
                steps:
                  - id: fork
                    type: parallel
                    branches:
                      - { name: a, steps: [{ id: a1, type: mcp.call }] }
                      - { name: b, steps: [{ id: b1, type: mcp.call }] }
            """);

        Assert.Equal("STEP_LIMIT", Assert.Throws<GnouGnouAnimationPlanException>(() =>
            GnouGnouAnimationPlanner.Build(steps, new GnouGnouAnimationOptions { MaxStepOccurrences = 1 })).Code);
        Assert.Equal("ACTOR_LIMIT", Assert.Throws<GnouGnouAnimationPlanException>(() =>
            GnouGnouAnimationPlanner.Build(dynamicCall, new GnouGnouAnimationOptions { MaxWorkflowActors = 1 })).Code);
        Assert.Equal("CLONE_LIMIT", Assert.Throws<GnouGnouAnimationPlanException>(() =>
            GnouGnouAnimationPlanner.Build(fork, new GnouGnouAnimationOptions { MaxClonesPerFork = 1 })).Code);
    }

    [Fact]
    public void Schedule_ScalesStableOffsetsAndRejectsUnsupportedSpeed()
    {
        var plan = GnouGnouAnimationPlanner.Build(Valid(TeamYaml), new GnouGnouAnimationOptions { Seed = 9 });
        var scheduled = WorkflowSimulationScheduler.Schedule(plan, 4);

        Assert.Equal(plan.Events.Count, scheduled.Count);
        Assert.Equal((long)Math.Round(plan.Events[^1].OffsetMs / 4d, MidpointRounding.AwayFromZero), scheduled[^1].OffsetMs);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowSimulationScheduler.Schedule(plan, 3));
    }

    private static WorkflowPreviewValidationResult Valid(string yaml)
    {
        var validation = WorkflowPreviewValidator.ParseAndValidate(yaml);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        return validation;
    }
}
