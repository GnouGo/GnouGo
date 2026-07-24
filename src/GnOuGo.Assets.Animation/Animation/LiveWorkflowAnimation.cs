using System.Globalization;
using System.Text;
using GnOuGo.Assets.Animation.Preview;
using GnOuGo.Assets.Bears;

namespace GnOuGo.Assets.Animation;

public enum AnimationExecutionSignalKind
{
    WorkflowStarted,
    WorkflowCompleted,
    StepStarted,
    StepCompleted,
    HumanInputWaiting,
    HumanInputResumed,
    Cancelled
}

public sealed record AnimationExecutionSignal
{
    public long Sequence { get; init; }
    public AnimationExecutionSignalKind Kind { get; init; }
    public string? WorkflowInstanceId { get; init; }
    public string? ParentWorkflowInstanceId { get; init; }
    public string? CallerStepOccurrenceId { get; init; }
    public string? WorkflowName { get; init; }
    public string? SourceText { get; init; }
    public string? StepOccurrenceId { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public SimulationStatus? Status { get; init; }
    public string? Message { get; init; }
}

public sealed record AnimationScenePatch
{
    public required string Id { get; init; }
    public required string SvgFragment { get; init; }
    public required AnimationSceneBounds Bounds { get; init; }
    public required IReadOnlyList<AnimationActor> Actors { get; init; }
    public required IReadOnlyList<AnimationStation> Stations { get; init; }
    public required IReadOnlyList<AnimationWorkflowLane> Lanes { get; init; }
    public required IReadOnlyList<AnimationFlowNode> Nodes { get; init; }
    public required IReadOnlyList<AnimationFlowEdge> Edges { get; init; }
}

public sealed record AnimationLiveUpdate
{
    public AnimationScenePatch? ScenePatch { get; init; }
    public SimulationEvent? Event { get; init; }
}

/// <summary>
/// Maps neutral, real execution signals onto a prepared animation scene.
/// This type deliberately has no dependency on GnOuGo.Flow.
/// </summary>
public sealed class WorkflowLiveAnimationSession
{
    private const int PersistentActionDurationMs = 86_400_000;
    private readonly GnouGnouAnimationPlan _plan;
    private readonly GnouGnouAnimationOptions _options;
    private readonly Dictionary<string, RuntimeWorkflow> _workflows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeStep> _steps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedLaneIds = new(StringComparer.Ordinal);
    private readonly List<AnimationScenePatch> _patches = [];
    private int _eventSequence;
    private int _dynamicLaneSequence;
    private int _completedVisibleSteps;

    public WorkflowLiveAnimationSession(
        GnouGnouAnimationPlan plan,
        GnouGnouAnimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;
        _options = options ?? new GnouGnouAnimationOptions
        {
            Seed = plan.Seed,
            Scene = plan.Scene
        };
    }

    public IReadOnlyList<AnimationScenePatch> ScenePatches => _patches;

    public IReadOnlyList<AnimationLiveUpdate> Apply(AnimationExecutionSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var updates = new List<AnimationLiveUpdate>();

        switch (signal.Kind)
        {
            case AnimationExecutionSignalKind.WorkflowStarted:
                StartWorkflow(signal, updates);
                break;
            case AnimationExecutionSignalKind.WorkflowCompleted:
                CompleteWorkflow(signal, updates);
                break;
            case AnimationExecutionSignalKind.StepStarted:
                StartStep(signal, updates);
                break;
            case AnimationExecutionSignalKind.StepCompleted:
                CompleteStep(signal, updates);
                break;
            case AnimationExecutionSignalKind.HumanInputWaiting:
                SetHumanWaiting(signal, updates);
                break;
            case AnimationExecutionSignalKind.HumanInputResumed:
                ResumeHumanInput(signal, updates);
                break;
            case AnimationExecutionSignalKind.Cancelled:
                Cancel(signal, updates);
                break;
        }

        return updates;
    }

    private void StartWorkflow(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        if (string.IsNullOrWhiteSpace(signal.WorkflowInstanceId))
            return;

        var isRoot = string.IsNullOrWhiteSpace(signal.ParentWorkflowInstanceId);
        var lane = FindAvailableLane(signal.WorkflowName, isRoot);
        AnimationScenePatch? patch = null;
        if (lane is null)
        {
            patch = DynamicScenePatchBuilder.Build(
                _plan,
                _patches,
                ++_dynamicLaneSequence,
                signal.WorkflowInstanceId,
                signal.WorkflowName ?? "dynamic-workflow",
                signal.SourceText,
                _options.Seed);
            _patches.Add(patch);
            lane = patch.Lanes[0];
            updates.Add(new AnimationLiveUpdate { ScenePatch = patch });
        }

        _usedLaneIds.Add(lane.Id);
        var runtime = new RuntimeWorkflow(
            signal.WorkflowInstanceId,
            signal.WorkflowName ?? lane.WorkflowName,
            lane,
            signal.ParentWorkflowInstanceId,
            signal.CallerStepOccurrenceId);
        _workflows[signal.WorkflowInstanceId] = runtime;

        if (isRoot)
        {
            Add(updates, SimulationEventTypes.SimulationStarted,
                workflow: runtime, status: SimulationStatus.Running,
                message: signal.Message ?? $"Workflow '{runtime.WorkflowName}' started.");
            Add(updates, SimulationEventTypes.ActorSpawned, _options.EffectDurationMs,
                workflow: runtime, actorId: lane.ActorId, x: lane.X + lane.Width / 2, y: lane.StartY,
                message: "The bearded master is ready.");
            Add(updates, SimulationEventTypes.TaskDropped, _options.EffectDurationMs,
                workflow: runtime, actorId: lane.ActorId, taskId: "task-root",
                x: lane.X + lane.Width / 2, y: lane.StartY - 75,
                message: "The input parcel arrives from the sky.");
            Add(updates, SimulationEventTypes.TaskPickedUp, _options.HandoffDurationMs,
                workflow: runtime, actorId: lane.ActorId, taskId: "task-root",
                message: "The master receives the input parcel.");
        }
        else
        {
            var parent = GetWorkflow(signal.ParentWorkflowInstanceId);
            Add(updates, SimulationEventTypes.ActorSpawned, _options.EffectDurationMs,
                workflow: runtime, actorId: lane.ActorId, x: lane.X + lane.Width / 2, y: lane.StartY,
                message: $"A GnOuGo joins workflow '{runtime.WorkflowName}'.");
            Add(updates, SimulationEventTypes.TaskHandedOff, _options.HandoffDurationMs,
                workflow: runtime,
                actorId: parent?.Lane.ActorId,
                targetActorId: lane.ActorId,
                taskId: "task-root",
                status: SimulationStatus.Running,
                message: $"The project parcel is handed to '{runtime.WorkflowName}'.");
        }

        Add(updates, SimulationEventTypes.WorkflowStarted,
            workflow: runtime, actorId: lane.ActorId, status: SimulationStatus.Running,
            message: signal.Message ?? $"Workflow '{runtime.WorkflowName}' started.");
    }

    private void CompleteWorkflow(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        var workflow = GetWorkflow(signal.WorkflowInstanceId);
        if (workflow is null)
            return;

        var status = signal.Status ?? SimulationStatus.Succeeded;
        Add(updates, SimulationEventTypes.WorkflowCompleted,
            workflow: workflow, actorId: workflow.Lane.ActorId, status: status,
            message: signal.Message ?? $"Workflow '{workflow.WorkflowName}' completed.");

        var parent = GetWorkflow(workflow.ParentWorkflowInstanceId);
        if (parent is not null)
        {
            Add(updates, SimulationEventTypes.TaskHandedOff, _options.HandoffDurationMs,
                workflow: workflow,
                actorId: workflow.Lane.ActorId,
                targetActorId: parent.Lane.ActorId,
                taskId: "task-root",
                status: status,
                message: status == SimulationStatus.Failed
                    ? "The failed parcel returns to the caller."
                    : "The completed parcel returns to the caller.");
            return;
        }

        var delivery = AllNodes().FirstOrDefault(node =>
            node.WorkflowInstanceId == workflow.Lane.WorkflowInstanceId
            && node.Kind == AnimationFlowNodeKind.Delivery);
        Add(updates, SimulationEventTypes.ActorMoved, _options.MoveDurationMs,
            workflow: workflow, actorId: workflow.Lane.ActorId,
            nodeId: delivery?.Id, x: delivery?.Position.X, y: delivery?.Position.Y,
            taskId: "task-root", status: status,
            message: "The master carries the project parcel to delivery.");
        Add(updates, SimulationEventTypes.OutputSent, _options.EffectDurationMs,
            workflow: workflow, actorId: workflow.Lane.ActorId,
            nodeId: delivery?.Id, taskId: "task-root",
            x: delivery?.Position.X ?? workflow.Lane.X + workflow.Lane.Width / 2,
            y: -80, status: status,
            message: status == SimulationStatus.Failed
                ? "The failed project parcel is returned skyward."
                : "The finished project parcel is delivered skyward.");
        Add(updates, SimulationEventTypes.SimulationCompleted,
            workflow: workflow, actorId: workflow.Lane.ActorId, status: status,
            message: status == SimulationStatus.Failed
                ? "Workflow execution failed."
                : "Workflow execution completed.");
    }

    private void StartStep(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        var workflow = GetWorkflow(signal.WorkflowInstanceId);
        if (workflow is null || string.IsNullOrWhiteSpace(signal.StepOccurrenceId))
            return;

        var node = FindNode(workflow, signal.StepId);
        var station = node?.StationId is null
            ? null
            : AllStations().FirstOrDefault(item => item.Id == node.StationId);
        var actorId = workflow.Lane.ActorId;
        string? cloneId = null;

        var concurrent = _steps.Values.FirstOrDefault(item =>
            item.WorkflowInstanceId == workflow.InstanceId
            && item.IsVisible
            && !item.Completed);
        if (concurrent is not null && node is not null)
        {
            cloneId = _plan.Actors.FirstOrDefault(actor =>
                actor.CloneOfActorId == workflow.Lane.ActorId
                && _steps.Values.All(step => !string.Equals(step.ActorId, actor.Id, StringComparison.Ordinal) || step.Completed))?.Id;
            if (cloneId is not null)
            {
                Add(updates, SimulationEventTypes.ActorCloned, _options.EffectDurationMs,
                    workflow: workflow, actorId: workflow.Lane.ActorId, targetActorId: cloneId,
                    branchId: signal.StepOccurrenceId, x: node.Position.X, y: node.Position.Y,
                    message: "Matrix clone starts a concurrent branch.");
                actorId = cloneId;
            }
        }

        var runtime = new RuntimeStep(
            signal.StepOccurrenceId,
            workflow.InstanceId,
            signal.StepId ?? "",
            signal.StepType ?? "",
            actorId,
            node,
            station,
            node is not null,
            cloneId);
        _steps[signal.StepOccurrenceId] = runtime;

        if (node is not null)
        {
            Add(updates, SimulationEventTypes.ActorMoved, _options.MoveDurationMs,
                workflow: workflow, actorId: actorId,
                step: runtime, nodeId: node.Id, stationId: station?.Id,
                edgeId: FindIncomingSelectedEdge(node.Id)?.Id,
                taskId: "task-root", x: node.Position.X, y: node.Position.Y,
                status: SimulationStatus.Running,
                message: $"GnOuGo walks to '{node.Label}'.");
        }

        Add(updates, SimulationEventTypes.StepStarted, PersistentActionDurationMs,
            workflow: workflow, actorId: actorId, step: runtime,
            nodeId: node?.Id, stationId: station?.Id,
            taskId: "task-root", status: SimulationStatus.Running,
            message: signal.Message ?? $"Step '{runtime.StepId}' started.");
    }

    private void CompleteStep(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        if (string.IsNullOrWhiteSpace(signal.StepOccurrenceId)
            || !_steps.TryGetValue(signal.StepOccurrenceId, out var step))
            return;

        var workflow = GetWorkflow(step.WorkflowInstanceId);
        if (workflow is null)
            return;

        step.Completed = true;
        var status = signal.Status ?? SimulationStatus.Succeeded;
        if (step.StepType.StartsWith("human.", StringComparison.OrdinalIgnoreCase)
            && status != SimulationStatus.Failed)
        {
            Add(updates, SimulationEventTypes.HumanInputResumed, _options.HandoffDurationMs,
                workflow: workflow, actorId: step.ActorId, step: step,
                nodeId: step.Node?.Id, stationId: step.Station?.Id,
                taskId: "task-root", status: SimulationStatus.Running,
                message: "Human input was received; GnOuGo resumes work.");
        }
        if (step.IsVisible && status == SimulationStatus.Succeeded)
            _completedVisibleSteps++;
        Add(updates, SimulationEventTypes.StepCompleted, _options.EffectDurationMs,
            workflow: workflow, actorId: step.ActorId, step: step,
            nodeId: step.Node?.Id, stationId: step.Station?.Id,
            taskId: "task-root", status: status,
            progressCurrent: _completedVisibleSteps,
            progressTotal: Math.Max(_completedVisibleSteps, CountVisibleSteps()),
            message: signal.Message ?? $"Step '{step.StepId}' {StatusText(status)}.");

        if (step.CloneActorId is not null)
        {
            Add(updates, SimulationEventTypes.ActorMerged, _options.EffectDurationMs,
                workflow: workflow, actorId: step.CloneActorId,
                targetActorId: workflow.Lane.ActorId,
                x: step.Node?.Position.X, y: step.Node?.Position.Y,
                status: status,
                message: "Matrix clone merges back into its GnOuGo.");
        }
    }

    private void SetHumanWaiting(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        if (string.IsNullOrWhiteSpace(signal.StepOccurrenceId)
            || !_steps.TryGetValue(signal.StepOccurrenceId, out var step))
            return;
        var workflow = GetWorkflow(step.WorkflowInstanceId);
        if (workflow is null)
            return;

        Add(updates, SimulationEventTypes.HumanInputWaiting, PersistentActionDurationMs,
            workflow: workflow, actorId: step.ActorId, step: step,
            nodeId: step.Node?.Id, stationId: step.Station?.Id,
            taskId: "task-root", status: SimulationStatus.Running,
            x: step.Node?.Position.X, y: step.Node?.Position.Y,
            message: signal.Message ?? "GnOuGo is calmly waiting for human input.");
        Add(updates, SimulationEventTypes.ActorWaiting, PersistentActionDurationMs,
            workflow: workflow, actorId: step.ActorId, step: step,
            nodeId: step.Node?.Id, stationId: step.Station?.Id,
            taskId: "task-root", status: SimulationStatus.Running,
            x: step.Node?.Position.X, y: step.Node?.Position.Y,
            message: "The parcel waits safely at the human-input counter.");
    }

    private void ResumeHumanInput(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        if (string.IsNullOrWhiteSpace(signal.StepOccurrenceId)
            || !_steps.TryGetValue(signal.StepOccurrenceId, out var step))
            return;
        var workflow = GetWorkflow(step.WorkflowInstanceId);
        if (workflow is null)
            return;

        Add(updates, SimulationEventTypes.HumanInputResumed, _options.HandoffDurationMs,
            workflow: workflow, actorId: step.ActorId, step: step,
            nodeId: step.Node?.Id, stationId: step.Station?.Id,
            taskId: "task-root", status: SimulationStatus.Running,
            message: signal.Message ?? "Human input was received; GnOuGo resumes work.");
    }

    private void Cancel(AnimationExecutionSignal signal, List<AnimationLiveUpdate> updates)
    {
        var workflow = GetWorkflow(signal.WorkflowInstanceId) ?? _workflows.Values.FirstOrDefault(item => item.ParentWorkflowInstanceId is null);
        Add(updates, SimulationEventTypes.SimulationCancelled,
            workflow: workflow, actorId: workflow?.Lane.ActorId,
            taskId: "task-root", status: SimulationStatus.Failed,
            message: signal.Message ?? "Workflow execution was cancelled.");
    }

    private AnimationWorkflowLane? FindAvailableLane(string? workflowName, bool root)
    {
        var lanes = AllLanes();
        if (root)
            return lanes.FirstOrDefault(static lane => lane.IsEntrypoint);

        return lanes.FirstOrDefault(lane =>
            !_usedLaneIds.Contains(lane.Id)
            && string.Equals(lane.WorkflowName, workflowName, StringComparison.Ordinal));
    }

    private AnimationFlowNode? FindNode(RuntimeWorkflow workflow, string? stepId) =>
        AllNodes().FirstOrDefault(node =>
            node.WorkflowInstanceId == workflow.Lane.WorkflowInstanceId
            && string.Equals(node.StepId, stepId, StringComparison.Ordinal));

    private AnimationFlowEdge? FindIncomingSelectedEdge(string nodeId) =>
        AllEdges().FirstOrDefault(edge =>
            edge.IsSelected && string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal));

    private RuntimeWorkflow? GetWorkflow(string? instanceId) =>
        instanceId is not null && _workflows.TryGetValue(instanceId, out var workflow) ? workflow : null;

    private IEnumerable<AnimationWorkflowLane> AllLanes() =>
        _plan.Lanes.Concat(_patches.SelectMany(static patch => patch.Lanes));

    private IEnumerable<AnimationFlowNode> AllNodes() =>
        _plan.Nodes.Concat(_patches.SelectMany(static patch => patch.Nodes));

    private IEnumerable<AnimationFlowEdge> AllEdges() =>
        _plan.Edges.Concat(_patches.SelectMany(static patch => patch.Edges));

    private IEnumerable<AnimationStation> AllStations() =>
        _plan.Stations.Concat(_patches.SelectMany(static patch => patch.Stations));

    private int CountVisibleSteps() => Math.Max(1, AllStations().Count(station =>
        station.Kind is AnimationStationKind.KeyboardDesk or AnimationStationKind.Human));

    private void Add(
        List<AnimationLiveUpdate> updates,
        string type,
        long durationMs = 0,
        RuntimeWorkflow? workflow = null,
        string? actorId = null,
        string? targetActorId = null,
        RuntimeStep? step = null,
        string? nodeId = null,
        string? stationId = null,
        string? edgeId = null,
        string? taskId = null,
        string? branchId = null,
        double? x = null,
        double? y = null,
        SimulationStatus? status = null,
        int? progressCurrent = null,
        int? progressTotal = null,
        string? message = null)
    {
        updates.Add(new AnimationLiveUpdate
        {
            Event = new SimulationEvent
            {
                Sequence = _eventSequence++,
                Type = type,
                OffsetMs = 0,
                DurationMs = durationMs,
                WorkflowInstanceId = workflow?.InstanceId,
                WorkflowName = workflow?.WorkflowName,
                ActorId = actorId,
                TargetActorId = targetActorId,
                StepId = step?.StepId,
                StepType = step?.StepType,
                StationId = stationId,
                NodeId = nodeId,
                EdgeId = edgeId,
                TaskId = taskId,
                BranchId = branchId,
                X = x,
                Y = y,
                Status = status,
                ProgressCurrent = progressCurrent,
                ProgressTotal = progressTotal,
                Message = message
            }
        });
    }

    private static string StatusText(SimulationStatus status) => status switch
    {
        SimulationStatus.Failed => "failed",
        SimulationStatus.Skipped => "was skipped",
        _ => "completed"
    };

    private sealed record RuntimeWorkflow(
        string InstanceId,
        string WorkflowName,
        AnimationWorkflowLane Lane,
        string? ParentWorkflowInstanceId,
        string? CallerStepOccurrenceId);

    private sealed class RuntimeStep(
        string occurrenceId,
        string workflowInstanceId,
        string stepId,
        string stepType,
        string actorId,
        AnimationFlowNode? node,
        AnimationStation? station,
        bool isVisible,
        string? cloneActorId)
    {
        public string OccurrenceId { get; } = occurrenceId;
        public string WorkflowInstanceId { get; } = workflowInstanceId;
        public string StepId { get; } = stepId;
        public string StepType { get; } = stepType;
        public string ActorId { get; } = actorId;
        public AnimationFlowNode? Node { get; } = node;
        public AnimationStation? Station { get; } = station;
        public bool IsVisible { get; } = isVisible;
        public string? CloneActorId { get; } = cloneActorId;
        public bool Completed { get; set; }
    }
}

internal static class DynamicScenePatchBuilder
{
    private const double LaneWidth = 440;
    private const double StepPitch = 260;

    public static AnimationScenePatch Build(
        GnouGnouAnimationPlan plan,
        IReadOnlyList<AnimationScenePatch> existing,
        int ordinal,
        string runtimeInstanceId,
        string workflowName,
        string? sourceText,
        int seed)
    {
        var safeInstance = SafeId(runtimeInstanceId);
        var laneId = $"lane-live-{safeInstance}";
        var actorId = $"actor-live-{safeInstance}";
        var x = plan.Bounds.Width - 160 + (ordinal - 1) * LaneWidth;
        var visible = ParseVisibleSteps(sourceText, workflowName);
        if (visible.Count == 0)
            visible.Add(new VisibleStep("dynamic-work", "workflow.execute", "Runtime work"));

        var height = Math.Max(
            plan.Bounds.Height,
            520 + visible.Count * StepPitch + 420);
        var width = Math.Max(
            plan.Bounds.Width,
            x + LaneWidth + 160);
        var lane = new AnimationWorkflowLane(
            laneId,
            runtimeInstanceId,
            workflowName,
            actorId,
            workflowName,
            x,
            LaneWidth,
            300,
            height - 200,
            false);
        var actor = new AnimationActor(
            actorId,
            workflowName,
            $"GnOuGo · {workflowName}",
            AnimationActorKind.Worker,
            unchecked(seed + ordinal * 7919),
            new AnimationPoint(x + LaneWidth / 2, 360));

        var nodes = new List<AnimationFlowNode>();
        var stations = new List<AnimationStation>();
        var edges = new List<AnimationFlowEdge>();
        var startId = $"node-live-{safeInstance}-start";
        nodes.Add(new AnimationFlowNode(
            startId, laneId, runtimeInstanceId, workflowName, "Start",
            AnimationFlowNodeKind.Start,
            new AnimationPoint(x + LaneWidth / 2, 380)));
        var previousId = startId;

        for (var index = 0; index < visible.Count; index++)
        {
            var step = visible[index];
            var safeStep = SafeId(step.Id);
            var nodeId = $"node-live-{safeInstance}-{safeStep}";
            var stationId = $"station-live-{safeInstance}-{safeStep}";
            var position = new AnimationPoint(x + LaneWidth / 2, 620 + index * StepPitch);
            nodes.Add(new AnimationFlowNode(
                nodeId, laneId, runtimeInstanceId, workflowName, step.Label,
                AnimationFlowNodeKind.Desk, position,
                step.Id, step.Type, stationId));
            stations.Add(new AnimationStation(
                stationId, step.Label,
                step.Type.StartsWith("human.", StringComparison.OrdinalIgnoreCase)
                    ? AnimationStationKind.Human
                    : AnimationStationKind.KeyboardDesk,
                position, runtimeInstanceId, workflowName, step.Id, step.Type));
            edges.Add(new AnimationFlowEdge(
                $"edge-live-{safeInstance}-{index}",
                previousId, nodeId, AnimationFlowEdgeKind.Sequence));
            previousId = nodeId;
        }

        var finishId = $"node-live-{safeInstance}-finish";
        nodes.Add(new AnimationFlowNode(
            finishId, laneId, runtimeInstanceId, workflowName, "Return",
            AnimationFlowNodeKind.Finish,
            new AnimationPoint(x + LaneWidth / 2, 620 + visible.Count * StepPitch)));
        edges.Add(new AnimationFlowEdge(
            $"edge-live-{safeInstance}-finish",
            previousId, finishId, AnimationFlowEdgeKind.Return));

        return new AnimationScenePatch
        {
            Id = $"patch-live-{safeInstance}",
            SvgFragment = RenderFragment(lane, actor, nodes, stations, edges),
            Bounds = new AnimationSceneBounds(width, height),
            Actors = [actor],
            Stations = stations,
            Lanes = [lane],
            Nodes = nodes,
            Edges = edges
        };
    }

    private static List<VisibleStep> ParseVisibleSteps(string? yaml, string workflowName)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return [];
        try
        {
            var document = WorkflowPreviewParser.Parse(yaml);
            var definition = document.Workflows.TryGetValue(workflowName, out var named)
                ? named
                : document.Entrypoint is not null && document.Workflows.TryGetValue(document.Entrypoint, out var entrypoint)
                    ? entrypoint
                    : document.Workflows.Values.FirstOrDefault();
            if (definition is null)
                return [];
            var result = new List<VisibleStep>();
            Collect(definition.Steps, result);
            return result;
        }
        catch (WorkflowPreviewParseException)
        {
            return [];
        }
    }

    private static void Collect(IEnumerable<WorkflowPreviewStep> steps, List<VisibleStep> result)
    {
        foreach (var step in steps)
        {
            if (WorkflowVisualFilter.IsLongRunningStepType(step.Type))
                result.Add(new VisibleStep(step.Id, step.Type, string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id));
            if (step.Steps is not null)
                Collect(step.Steps, result);
            if (step.Branches is not null)
                foreach (var branch in step.Branches)
                    Collect(branch.Steps, result);
            if (step.Cases is not null)
                foreach (var switchCase in step.Cases)
                    Collect(switchCase.Steps, result);
            if (step.Default is not null)
                Collect(step.Default, result);
        }
    }

    private static string RenderFragment(
        AnimationWorkflowLane lane,
        AnimationActor actor,
        IReadOnlyList<AnimationFlowNode> nodes,
        IReadOnlyList<AnimationStation> stations,
        IReadOnlyList<AnimationFlowEdge> edges)
    {
        var builder = new StringBuilder();
        builder.Append("<g id=\"").Append(Escape(lane.Id)).Append("\" class=\"workflow-lane\" data-live-patch=\"true\">")
            .Append("<rect x=\"").Append(Number(lane.X)).Append("\" y=\"250\" width=\"").Append(Number(lane.Width))
            .Append("\" height=\"").Append(Number(lane.EndY - 180)).Append("\" rx=\"42\" fill=\"#fff\" opacity=\".38\"/>")
            .Append("<text class=\"lane-label\" x=\"").Append(Number(lane.X + 34)).Append("\" y=\"300\">")
            .Append(Escape(lane.Label)).Append("</text></g>");

        foreach (var edge in edges)
        {
            var from = nodes.First(node => node.Id == edge.FromNodeId).Position;
            var to = nodes.First(node => node.Id == edge.ToNodeId).Position;
            builder.Append("<g id=\"").Append(Escape(edge.Id)).Append("\" class=\"flow-edge\">")
                .Append("<path data-route-path=\"true\" class=\"route-surface\" d=\"M")
                .Append(Number(from.X)).Append(' ').Append(Number(from.Y))
                .Append(" C").Append(Number(from.X + 48)).Append(' ').Append(Number((from.Y + to.Y) / 2))
                .Append(' ').Append(Number(to.X - 48)).Append(' ').Append(Number((from.Y + to.Y) / 2))
                .Append(' ').Append(Number(to.X)).Append(' ').Append(Number(to.Y)).Append("\"/></g>");
        }

        foreach (var node in nodes)
        {
            builder.Append("<g id=\"").Append(Escape(node.Id)).Append("\" class=\"flow-node\" transform=\"translate(")
                .Append(Number(node.Position.X)).Append(' ').Append(Number(node.Position.Y)).Append(")\">");
            if (node.Kind is AnimationFlowNodeKind.Start or AnimationFlowNodeKind.Finish)
                builder.Append("<path class=\"isometric-sign\" d=\"M-64 0L0-28 64 0 0 28Z\"/>");
            builder.Append("<text class=\"node-label\" y=\"7\">").Append(Escape(node.Label)).Append("</text></g>");
        }

        foreach (var station in stations)
        {
            builder.Append("<g id=\"").Append(Escape(station.Id)).Append("\" class=\"workflow-station\" data-step-id=\"")
                .Append(Escape(station.StepId ?? "")).Append("\" transform=\"translate(")
                .Append(Number(station.Position.X)).Append(' ').Append(Number(station.Position.Y)).Append(")\">")
                .Append("<g class=\"isometric-desk\"><path class=\"desk-top\" d=\"M-118 24L38-34 132-4-32 54Z\"/>")
                .Append("<path class=\"desk-front\" d=\"M-118 24L-32 54 132-4V18L-32 77-118 46Z\"/>")
                .Append("<g class=\"isometric-laptop\"><path class=\"laptop-shell\" d=\"M-43-45L35-21V25L-43 1Z\"/>")
                .Append("<path class=\"laptop-screen\" d=\"M-35-36L27-17V16L-35-3Z\"/>")
                .Append("<path class=\"laptop-shell\" d=\"M-43 1L35 25 71 13-9-12Z\"/></g></g>")
                .Append("<text class=\"station-label\" x=\"0\" y=\"112\">").Append(Escape(station.Label)).Append("</text></g>");
        }

        var bear = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Seed = actor.VisualSeed,
            Size = 256,
            SvgIdPrefix = actor.Id,
            Theme = GnouGnouBearTheme.Transparent,
            Role = GnouGnouBearRole.Coder,
            Emotion = GnouGnouBearEmotion.Focused,
            State = GnouGnouBearState.Idle,
            Accessory = GnouGnouBearAccessory.Laptop,
            HasHeadphones = true,
            EnableAnimationRig = true,
            Title = actor.Label
        }).Replace(
            "<svg width=\"256\" height=\"256\"",
            "<svg x=\"-90\" y=\"-180\" width=\"180\" height=\"180\"",
            StringComparison.Ordinal);
        builder.Append("<g id=\"").Append(Escape(actor.Id)).Append("\" class=\"gnougo-actor\" data-visible=\"false\" data-visual-seed=\"")
            .Append(actor.VisualSeed.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-workflow=\"").Append(Escape(actor.WorkflowName))
            .Append("\" transform=\"translate(").Append(Number(actor.Home.X)).Append(' ').Append(Number(actor.Home.Y)).Append(")\">")
            .Append(bear).Append("</g>");
        return builder.ToString();
    }

    private static string SafeId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        return builder.Length == 0 ? "runtime" : builder.ToString();
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private sealed record VisibleStep(string Id, string Type, string Label);
}
