using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation;

internal static class WorkflowSceneLayoutBuilder
{
    private const double MinimumCanvasWidth = 1600;
    private const double MinimumCanvasHeight = 900;
    private const double CanvasMargin = 160;
    private const double LaneGap = 80;
    private const double MinimumLaneWidth = 440;
    private const double BranchPitch = 300;
    private const double StepPitch = 260;
    private const double ControlPitch = 190;
    private const double ActorOffsetY = 108;

    public static WorkflowSceneLayout Build(
        WorkflowPreviewDocument document,
        string entrypoint,
        IReadOnlyList<AnimationActor> actors,
        IReadOnlyList<SimulationEvent> events)
    {
        var context = new LayoutContext(document, entrypoint, actors, events);
        return context.Build();
    }

    private sealed class LayoutContext
    {
        private readonly WorkflowPreviewDocument _document;
        private readonly string _entrypoint;
        private readonly IReadOnlyList<AnimationActor> _actors;
        private readonly IReadOnlyList<SimulationEvent> _events;
        private readonly List<AnimationWorkflowLane> _lanes = [];
        private readonly List<AnimationFlowNode> _nodes = [];
        private readonly List<AnimationFlowEdge> _edges = [];
        private readonly List<AnimationStation> _stations = [];
        private readonly Dictionary<string, AnimationFlowNode> _nodesById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _actorInstances = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _actorLaneIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AnimationPoint> _actorHomes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _stepNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _joinNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _returnNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _finishNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _deliveryNodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _callChildren = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _selectedChoices = new(StringComparer.Ordinal);
        private readonly HashSet<string> _skippedSteps = new(StringComparer.Ordinal);
        private readonly HashSet<string> _laidOutActors = new(StringComparer.Ordinal);
        private int _edgeCounter;
        private double _nextLaneX = CanvasMargin;

        public LayoutContext(
            WorkflowPreviewDocument document,
            string entrypoint,
            IReadOnlyList<AnimationActor> actors,
            IReadOnlyList<SimulationEvent> events)
        {
            _document = document;
            _entrypoint = entrypoint;
            _actors = actors;
            _events = events;
            IndexRuntime();
            AllocateLanes();
        }

        public WorkflowSceneLayout Build()
        {
            var master = _actors.First(static actor => actor.Kind == AnimationActorKind.Master);
            LayoutInstance(master.Id, 180);

            foreach (var actor in _actors.Where(static actor => actor.Kind == AnimationActorKind.Worker))
            {
                if (!_laidOutActors.Contains(actor.Id))
                    LayoutInstance(actor.Id, 180);
            }

            var maximumX = _lanes.Count == 0
                ? MinimumCanvasWidth - CanvasMargin
                : _lanes.Max(static lane => lane.X + lane.Width / 2);
            var maximumY = _nodes.Count == 0
                ? MinimumCanvasHeight - CanvasMargin
                : _nodes.Max(static node => node.Position.Y) + 230;
            var bounds = new AnimationSceneBounds(
                Math.Max(MinimumCanvasWidth, maximumX + CanvasMargin),
                Math.Max(MinimumCanvasHeight, maximumY + CanvasMargin));

            return new WorkflowSceneLayout(
                _lanes.ToArray(),
                _nodes.ToArray(),
                _edges.ToArray(),
                _stations.ToArray(),
                bounds,
                _actorInstances,
                _actorLaneIds,
                _actorHomes,
                _nodesById,
                _stepNodes,
                _joinNodes,
                _returnNodes,
                _finishNodes,
                _deliveryNodes);
        }

        private void IndexRuntime()
        {
            foreach (var workflowStarted in _events.Where(static item =>
                         item.Type == SimulationEventTypes.WorkflowStarted
                         && item.ActorId is not null
                         && item.WorkflowInstanceId is not null))
            {
                _actorInstances.TryAdd(workflowStarted.ActorId!, workflowStarted.WorkflowInstanceId!);
            }

            var master = _actors.FirstOrDefault(static actor => actor.Kind == AnimationActorKind.Master);
            if (master is not null)
                _actorInstances.TryAdd(master.Id, "workflow-master");

            foreach (var actor in _actors.Where(static actor => actor.Kind == AnimationActorKind.Worker))
                _actorInstances.TryAdd(actor.Id, $"workflow-{actor.Id}");

            foreach (var clone in _actors.Where(static actor => actor.Kind == AnimationActorKind.Clone))
            {
                if (clone.CloneOfActorId is not null
                    && _actorInstances.TryGetValue(clone.CloneOfActorId, out var instance))
                    _actorInstances[clone.Id] = instance;
            }

            foreach (var handoff in _events.Where(static item =>
                         item.Type == SimulationEventTypes.TaskHandedOff
                         && item.ActorId is not null
                         && item.TargetActorId is not null
                         && item.WorkflowInstanceId is not null
                         && item.StepId is not null))
            {
                var target = _actors.FirstOrDefault(actor => actor.Id == handoff.TargetActorId);
                if (target?.Kind != AnimationActorKind.Worker)
                    continue;
                // Matrix clones share their caller workflow instance. Index calls by the
                // logical invocation/step rather than by the transient visual actor so a
                // call made from a parallel clone still receives its own child lane.
                var key = CallKey(handoff.WorkflowInstanceId!, handoff.StepId!);
                if (!_callChildren.TryGetValue(key, out var children))
                {
                    children = [];
                    _callChildren[key] = children;
                }
                if (!children.Contains(handoff.TargetActorId!, StringComparer.Ordinal))
                    children.Add(handoff.TargetActorId!);
            }

            foreach (var decision in _events.Where(static item =>
                         item.Type == SimulationEventTypes.DecisionSimulated
                         && item.WorkflowInstanceId is not null
                         && item.StepId is not null
                         && item.BranchId is not null))
                _selectedChoices[StepKey(decision.WorkflowInstanceId!, decision.StepId!)] = decision.BranchId!;

            foreach (var skipped in _events.Where(static item =>
                         item.Type == SimulationEventTypes.StepSkipped
                         && item.WorkflowInstanceId is not null
                         && item.StepId is not null))
                _skippedSteps.Add(StepKey(skipped.WorkflowInstanceId!, skipped.StepId!));
        }

        private void AllocateLanes()
        {
            foreach (var actor in _actors.Where(static actor => actor.Kind != AnimationActorKind.Clone))
            {
                var instance = _actorInstances[actor.Id];
                var definition = _document.Workflows.TryGetValue(actor.WorkflowName, out var workflow)
                    ? workflow
                    : null;
                var width = definition is null ? MinimumLaneWidth : CalculateLaneWidth(definition.Steps);
                var center = _nextLaneX + width / 2;
                var laneId = $"lane-{Slug(instance)}";
                _actorLaneIds[actor.Id] = laneId;
                _lanes.Add(new AnimationWorkflowLane(
                    laneId,
                    instance,
                    actor.WorkflowName,
                    actor.Id,
                    actor.Kind == AnimationActorKind.Master
                        ? $"Master · {actor.WorkflowName}"
                        : actor.WorkflowName,
                    center,
                    width,
                    180,
                    180,
                    actor.Kind == AnimationActorKind.Master));
                _nextLaneX += width + LaneGap;
            }

            foreach (var clone in _actors.Where(static actor => actor.Kind == AnimationActorKind.Clone))
            {
                if (clone.CloneOfActorId is not null
                    && _actorLaneIds.TryGetValue(clone.CloneOfActorId, out var laneId))
                    _actorLaneIds[clone.Id] = laneId;
            }
        }

        private double LayoutInstance(string actorId, double startY)
        {
            if (!_laidOutActors.Add(actorId))
                return _lanes.First(lane => lane.Id == _actorLaneIds[actorId]).EndY;

            var actor = _actors.First(item => item.Id == actorId);
            var laneIndex = _lanes.FindIndex(lane => lane.Id == _actorLaneIds[actorId]);
            var lane = _lanes[laneIndex];
            var instance = _actorInstances[actorId];
            var start = AddNode(
                NodeId(instance, "start"),
                lane,
                "Start",
                AnimationFlowNodeKind.Start,
                new AnimationPoint(lane.X, startY));
            _actorHomes[actorId] = ActorPoint(start);

            double endY;
            string lastNodeId;
            if (_document.Workflows.TryGetValue(actor.WorkflowName, out var workflow))
            {
                var result = LayoutStepList(workflow.Steps, actorId, lane, start.Id, startY + StepPitch, lane.X, null, true);
                endY = result.EndY;
                lastNodeId = result.LastNodeId;
            }
            else
            {
                var synthetic = AddDesk(
                    lane,
                    instance,
                    actor.WorkflowName,
                    "dynamic-work",
                    "workflow.execute",
                    "Simulated workflow",
                    new AnimationPoint(lane.X, startY + StepPitch),
                    true);
                AddEdge(start.Id, synthetic.Node.Id, AnimationFlowEdgeKind.Sequence);
                endY = synthetic.Node.Position.Y + StepPitch;
                lastNodeId = synthetic.Node.Id;
            }

            var finish = AddNode(
                NodeId(instance, "finish"),
                lane,
                "Workflow complete",
                AnimationFlowNodeKind.Finish,
                new AnimationPoint(lane.X, endY));
            _finishNodes[instance] = finish.Id;
            AddEdge(lastNodeId, finish.Id, AnimationFlowEdgeKind.Sequence);
            endY = finish.Position.Y;

            if (actor.Kind == AnimationActorKind.Master)
            {
                var deliveryStationId = $"station-{NodeId(instance, "delivery")}";
                var delivery = AddNode(
                    NodeId(instance, "delivery"),
                    lane,
                    "Delivery dock",
                    AnimationFlowNodeKind.Delivery,
                    new AnimationPoint(lane.X, endY + StepPitch),
                    stationId: deliveryStationId);
                _deliveryNodes[instance] = delivery.Id;
                AddEdge(finish.Id, delivery.Id, AnimationFlowEdgeKind.Sequence);
                _stations.Add(new AnimationStation(
                    deliveryStationId,
                    delivery.Label,
                    AnimationStationKind.DeliveryDock,
                    ActorPoint(delivery),
                    instance,
                    actor.WorkflowName));
                endY = delivery.Position.Y;
            }

            _lanes[laneIndex] = lane with { StartY = startY, EndY = endY };
            return endY;
        }

        private StepLayoutResult LayoutStepList(
            IReadOnlyList<WorkflowPreviewStep> steps,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double startY,
            double centerX,
            string? branchId,
            bool selected)
        {
            var cursorY = startY;
            var lastNodeId = previousNodeId;
            foreach (var step in steps)
            {
                var result = LayoutStep(step, actorId, lane, lastNodeId, cursorY, centerX, branchId, selected);
                lastNodeId = result.LastNodeId;
                cursorY = result.EndY;
            }
            return new StepLayoutResult(lastNodeId, cursorY);
        }

        private StepLayoutResult LayoutStep(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            string? branchId,
            bool routeSelected)
        {
            if (!WorkflowVisualFilter.StepContainsVisibleWork(_document, step))
                return new StepLayoutResult(previousNodeId, y);

            var instance = _actorInstances[actorId];
            var selected = routeSelected && !_skippedSteps.Contains(StepKey(instance, step.Id));

            return step.Type switch
            {
                "parallel" => LayoutParallel(step, actorId, lane, previousNodeId, y, centerX, selected),
                "loop.parallel" => LayoutParallelLoop(step, actorId, lane, previousNodeId, y, centerX, selected),
                "switch" => LayoutSwitch(step, actorId, lane, previousNodeId, y, centerX, selected),
                "sequence" => LayoutSequence(step, actorId, lane, previousNodeId, y, centerX, selected),
                "loop.sequential" => LayoutSequentialLoop(step, actorId, lane, previousNodeId, y, centerX, selected),
                "workflow.call" or "workflow.route" or "workflow.execute" =>
                    LayoutCall(step, actorId, lane, previousNodeId, y, centerX, branchId, selected),
                _ => LayoutAtomic(step, lane, instance, previousNodeId, y, centerX, branchId, selected)
            };
        }

        private StepLayoutResult LayoutAtomic(
            WorkflowPreviewStep step,
            AnimationWorkflowLane lane,
            string instance,
            string previousNodeId,
            double y,
            double centerX,
            string? branchId,
            bool selected)
        {
            var desk = AddDesk(
                lane,
                instance,
                lane.WorkflowName,
                step.Id,
                step.Type,
                step.Id,
                new AnimationPoint(centerX, y),
                selected,
                branchId,
                step.Type.StartsWith("human.", StringComparison.OrdinalIgnoreCase)
                    ? AnimationStationKind.Human
                    : AnimationStationKind.KeyboardDesk);
            _stepNodes.TryAdd(StepKey(instance, step.Id), desk.Node.Id);
            AddEdge(previousNodeId, desk.Node.Id, AnimationFlowEdgeKind.Sequence, isSelected: selected);
            return new StepLayoutResult(desk.Node.Id, y + StepPitch);
        }

        private StepLayoutResult LayoutSequence(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            bool selected)
        {
            return LayoutStepList(
                step.Steps ?? [],
                actorId,
                lane,
                previousNodeId,
                y,
                centerX,
                null,
                selected);
        }

        private StepLayoutResult LayoutSequentialLoop(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            bool selected)
        {
            var instance = _actorInstances[actorId];
            var loop = AddControl(step, lane, instance, AnimationFlowNodeKind.Loop, new AnimationPoint(centerX, y), selected);
            AddEdge(previousNodeId, loop.Id, AnimationFlowEdgeKind.Sequence, isSelected: selected);
            var body = LayoutStepList(step.Steps ?? [], actorId, lane, loop.Id, y + ControlPitch, centerX, "loop", selected);
            var exit = AddNode(
                $"{NodeId(instance, step.Id)}-join",
                lane,
                "Loop exit",
                AnimationFlowNodeKind.Join,
                new AnimationPoint(centerX, Math.Max(y + StepPitch, body.EndY)));
            _joinNodes[StepKey(instance, step.Id)] = exit.Id;
            AddEdge(body.LastNodeId, loop.Id, AnimationFlowEdgeKind.Loop, "repeat", selected);
            AddEdge(loop.Id, exit.Id, AnimationFlowEdgeKind.Sequence, "exit", selected);
            return new StepLayoutResult(exit.Id, exit.Position.Y + StepPitch);
        }

        private StepLayoutResult LayoutParallel(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            bool selected)
        {
            var branches = (step.Branches ?? [])
                .Where(branch => WorkflowVisualFilter.StepsContainVisibleWork(_document, branch.Steps))
                .ToArray();
            return LayoutBranches(
                step,
                branches.Select(static branch => ((string?)branch.Name, (IReadOnlyList<WorkflowPreviewStep>)branch.Steps)).ToArray(),
                actorId,
                lane,
                previousNodeId,
                y,
                centerX,
                selected,
                AnimationFlowNodeKind.Fork);
        }

        private StepLayoutResult LayoutParallelLoop(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            bool selected)
        {
            var branches = new (string? Label, IReadOnlyList<WorkflowPreviewStep> Steps)[]
            {
                ("iterations", step.Steps ?? [])
            };
            return LayoutBranches(step, branches, actorId, lane, previousNodeId, y, centerX, selected, AnimationFlowNodeKind.Loop);
        }

        private StepLayoutResult LayoutSwitch(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            bool selected)
        {
            var choices = new List<(string? Label, IReadOnlyList<WorkflowPreviewStep> Steps)>();
            if (step.Cases is { } cases)
                choices.AddRange(cases
                    .Where(item => WorkflowVisualFilter.StepsContainVisibleWork(_document, item.Steps))
                    .Select((item, index) => ((string?)(item.Value ?? item.When ?? $"case {index + 1}"), (IReadOnlyList<WorkflowPreviewStep>)item.Steps)));
            if (step.Default is { Count: > 0 } defaults)
            {
                if (WorkflowVisualFilter.StepsContainVisibleWork(_document, defaults))
                    choices.Add(("default", defaults));
            }
            return LayoutBranches(step, choices, actorId, lane, previousNodeId, y, centerX, selected, AnimationFlowNodeKind.Decision, isSwitch: true);
        }

        private StepLayoutResult LayoutBranches(
            WorkflowPreviewStep owner,
            IReadOnlyList<(string? Label, IReadOnlyList<WorkflowPreviewStep> Steps)> branches,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            bool selected,
            AnimationFlowNodeKind forkKind,
            bool isSwitch = false)
        {
            var instance = _actorInstances[actorId];
            var fork = AddControl(owner, lane, instance, forkKind, new AnimationPoint(centerX, y), selected);
            AddEdge(previousNodeId, fork.Id, AnimationFlowEdgeKind.Sequence, isSelected: selected);
            var branchStartY = y + ControlPitch;
            var branchResults = new List<StepLayoutResult>();
            var branchCount = Math.Max(1, branches.Count);
            var startX = centerX - (branchCount - 1) * BranchPitch / 2;
            _selectedChoices.TryGetValue(StepKey(instance, owner.Id), out var selectedChoice);

            for (var index = 0; index < branches.Count; index++)
            {
                var label = branches[index].Label ?? $"branch {index + 1}";
                var branchSelected = selected && (!isSwitch || string.Equals(label, selectedChoice, StringComparison.OrdinalIgnoreCase));
                var branchX = startX + index * BranchPitch;
                var result = LayoutStepList(
                    branches[index].Steps,
                    actorId,
                    lane,
                    fork.Id,
                    branchStartY,
                    branchX,
                    label,
                    branchSelected);
                if (branches[index].Steps.Count == 0)
                    AddEdge(fork.Id, result.LastNodeId, AnimationFlowEdgeKind.Branch, label, branchSelected);
                branchResults.Add(result);
            }

            var joinY = branchResults.Count == 0
                ? y + StepPitch
                : Math.Max(y + StepPitch, branchResults.Max(static result => result.EndY));
            var join = AddNode(
                $"{NodeId(instance, owner.Id)}-join",
                lane,
                isSwitch ? "Route join" : "Parallel join",
                AnimationFlowNodeKind.Join,
                new AnimationPoint(centerX, joinY),
                stepId: owner.Id,
                stepType: owner.Type,
                isSelected: selected);
            _joinNodes[StepKey(instance, owner.Id)] = join.Id;

            for (var index = 0; index < branchResults.Count; index++)
            {
                var label = branches[index].Label ?? $"branch {index + 1}";
                var branchSelected = selected && (!isSwitch || string.Equals(label, selectedChoice, StringComparison.OrdinalIgnoreCase));
                AddEdge(branchResults[index].LastNodeId, join.Id, AnimationFlowEdgeKind.Branch, label, branchSelected);
            }
            return new StepLayoutResult(join.Id, joinY + StepPitch);
        }

        private StepLayoutResult LayoutCall(
            WorkflowPreviewStep step,
            string actorId,
            AnimationWorkflowLane lane,
            string previousNodeId,
            double y,
            double centerX,
            string? branchId,
            bool selected)
        {
            var instance = _actorInstances[actorId];
            var desk = AddDesk(
                lane,
                instance,
                lane.WorkflowName,
                step.Id,
                step.Type,
                step.Id,
                new AnimationPoint(centerX, y),
                selected,
                branchId,
                AnimationStationKind.HandoffDesk,
                AnimationFlowNodeKind.WorkflowCall);
            _stepNodes.TryAdd(StepKey(instance, step.Id), desk.Node.Id);
            AddEdge(previousNodeId, desk.Node.Id, AnimationFlowEdgeKind.Sequence, isSelected: selected);

            var callKey = CallKey(instance, step.Id);
            _callChildren.TryGetValue(callKey, out var children);
            children ??= [];
            var childEnds = new List<double>();
            foreach (var childActorId in children)
            {
                var childEnd = LayoutInstance(childActorId, y);
                childEnds.Add(childEnd);
                var childInstance = _actorInstances[childActorId];
                var childStart = NodeId(childInstance, "start");
                var childFinish = _finishNodes[childInstance];
                AddEdge(desk.Node.Id, childStart, AnimationFlowEdgeKind.Handoff, "handoff", selected);
                // Return edge is connected after the caller return node has been placed.
                _returnNodes[$"{StepKey(instance, step.Id)}|child|{childActorId}"] = childFinish;
            }

            var returnY = Math.Max(y + StepPitch, childEnds.DefaultIfEmpty(y).Max() + ControlPitch);
            var returnNode = AddNode(
                $"{NodeId(instance, step.Id)}-return",
                lane,
                "Work returned",
                AnimationFlowNodeKind.Return,
                new AnimationPoint(centerX, returnY),
                stepId: step.Id,
                stepType: step.Type,
                branchId: branchId,
                isSelected: selected);
            _returnNodes[StepKey(instance, step.Id)] = returnNode.Id;
            AddEdge(desk.Node.Id, returnNode.Id, AnimationFlowEdgeKind.Waiting, "caller waits", selected);
            foreach (var childActorId in children)
            {
                var childFinish = _returnNodes[$"{StepKey(instance, step.Id)}|child|{childActorId}"];
                AddEdge(childFinish, returnNode.Id, AnimationFlowEdgeKind.Return, "result", selected);
            }
            return new StepLayoutResult(returnNode.Id, returnY + StepPitch);
        }

        private (AnimationFlowNode Node, AnimationStation Station) AddDesk(
            AnimationWorkflowLane lane,
            string instance,
            string workflowName,
            string stepId,
            string stepType,
            string label,
            AnimationPoint position,
            bool selected,
            string? branchId = null,
            AnimationStationKind stationKind = AnimationStationKind.KeyboardDesk,
            AnimationFlowNodeKind nodeKind = AnimationFlowNodeKind.Desk)
        {
            var stationId = $"station-{Slug(instance)}-{Slug(stepId)}";
            var node = AddNode(
                NodeId(instance, stepId),
                lane,
                label,
                nodeKind,
                position,
                stepId,
                stepType,
                stationId,
                branchId,
                selected);
            var station = new AnimationStation(
                stationId,
                label,
                stationKind,
                ActorPoint(node),
                instance,
                workflowName,
                stepId,
                stepType);
            _stations.Add(station);
            return (node, station);
        }

        private AnimationFlowNode AddControl(
            WorkflowPreviewStep step,
            AnimationWorkflowLane lane,
            string instance,
            AnimationFlowNodeKind kind,
            AnimationPoint position,
            bool selected)
        {
            var node = AddNode(
                NodeId(instance, step.Id),
                lane,
                step.Id,
                kind,
                position,
                step.Id,
                step.Type,
                isSelected: selected);
            _stepNodes.TryAdd(StepKey(instance, step.Id), node.Id);
            return node;
        }

        private AnimationFlowNode AddNode(
            string id,
            AnimationWorkflowLane lane,
            string label,
            AnimationFlowNodeKind kind,
            AnimationPoint position,
            string? stepId = null,
            string? stepType = null,
            string? stationId = null,
            string? branchId = null,
            bool isSelected = true)
        {
            if (_nodesById.TryGetValue(id, out var existing))
                return existing;
            var node = new AnimationFlowNode(
                id,
                lane.Id,
                lane.WorkflowInstanceId,
                lane.WorkflowName,
                label,
                kind,
                position,
                stepId,
                stepType,
                stationId,
                branchId,
                isSelected);
            _nodes.Add(node);
            _nodesById[id] = node;
            return node;
        }

        private void AddEdge(
            string from,
            string to,
            AnimationFlowEdgeKind kind,
            string? label = null,
            bool isSelected = true)
        {
            if (string.Equals(from, to, StringComparison.Ordinal))
                return;
            _edgeCounter++;
            _edges.Add(new AnimationFlowEdge($"edge-{_edgeCounter}", from, to, kind, label, isSelected));
        }

        private static AnimationPoint ActorPoint(AnimationFlowNode node) =>
            new(node.Position.X, node.Position.Y + ActorOffsetY);

        private double CalculateLaneWidth(IReadOnlyList<WorkflowPreviewStep> steps)
        {
            var maximumBranches = 1;
            foreach (var step in EnumerateSteps(steps)
                         .Where(step => WorkflowVisualFilter.StepContainsVisibleWork(_document, step)))
            {
                var count = step.Branches?.Count(branch =>
                                WorkflowVisualFilter.StepsContainVisibleWork(_document, branch.Steps))
                    ?? (step.Cases?.Count(switchCase =>
                            WorkflowVisualFilter.StepsContainVisibleWork(_document, switchCase.Steps)) ?? 0)
                    + (step.Default is { Count: > 0 } defaults
                       && WorkflowVisualFilter.StepsContainVisibleWork(_document, defaults)
                        ? 1
                        : 0);
                maximumBranches = Math.Max(maximumBranches, count);
            }
            return Math.Max(MinimumLaneWidth, maximumBranches * BranchPitch + 160);
        }

        private static IEnumerable<WorkflowPreviewStep> EnumerateSteps(IEnumerable<WorkflowPreviewStep> steps)
        {
            foreach (var step in steps)
            {
                yield return step;
                if (step.Steps is { } children)
                    foreach (var child in EnumerateSteps(children))
                        yield return child;
                if (step.Branches is { } branches)
                    foreach (var branch in branches)
                        foreach (var child in EnumerateSteps(branch.Steps))
                            yield return child;
                if (step.Cases is { } cases)
                    foreach (var switchCase in cases)
                        foreach (var child in EnumerateSteps(switchCase.Steps))
                            yield return child;
                if (step.Default is { } defaults)
                    foreach (var child in EnumerateSteps(defaults))
                        yield return child;
            }
        }

        private static string StepKey(string instance, string stepId) => $"{instance}|{stepId}";
        private static string CallKey(string instance, string stepId) => $"{instance}|{stepId}";
        private static string NodeId(string instance, string name) => $"node-{Slug(instance)}-{Slug(name)}";

        private static string Slug(string value)
        {
            var characters = value.Select(static character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-')
                .ToArray();
            return new string(characters).Trim('-');
        }

        private readonly record struct StepLayoutResult(string LastNodeId, double EndY);
    }
}

internal sealed record WorkflowSceneLayout(
    IReadOnlyList<AnimationWorkflowLane> Lanes,
    IReadOnlyList<AnimationFlowNode> Nodes,
    IReadOnlyList<AnimationFlowEdge> Edges,
    IReadOnlyList<AnimationStation> Stations,
    AnimationSceneBounds Bounds,
    IReadOnlyDictionary<string, string> ActorInstances,
    IReadOnlyDictionary<string, string> ActorLaneIds,
    IReadOnlyDictionary<string, AnimationPoint> ActorHomes,
    IReadOnlyDictionary<string, AnimationFlowNode> NodesById,
    IReadOnlyDictionary<string, string> StepNodes,
    IReadOnlyDictionary<string, string> JoinNodes,
    IReadOnlyDictionary<string, string> ReturnNodes,
    IReadOnlyDictionary<string, string> FinishNodes,
    IReadOnlyDictionary<string, string> DeliveryNodes)
{
    public string? FindStepNode(string? workflowInstanceId, string? stepId)
    {
        if (workflowInstanceId is null || stepId is null)
            return null;
        return StepNodes.TryGetValue($"{workflowInstanceId}|{stepId}", out var nodeId) ? nodeId : null;
    }

    public string? FindJoinNode(string? workflowInstanceId, string? stepId)
    {
        if (workflowInstanceId is null || stepId is null)
            return null;
        return JoinNodes.TryGetValue($"{workflowInstanceId}|{stepId}", out var nodeId) ? nodeId : null;
    }

    public string? FindReturnNode(string? workflowInstanceId, string? stepId)
    {
        if (workflowInstanceId is null || stepId is null)
            return null;
        return ReturnNodes.TryGetValue($"{workflowInstanceId}|{stepId}", out var nodeId) ? nodeId : null;
    }

    public AnimationPoint ActorPointForNode(string nodeId)
    {
        var node = NodesById[nodeId];
        var station = Stations.FirstOrDefault(item => item.Id == node.StationId);
        return station?.Position ?? new AnimationPoint(node.Position.X, node.Position.Y + 108);
    }
}
