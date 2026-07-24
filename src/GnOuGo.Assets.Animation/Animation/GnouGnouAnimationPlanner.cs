using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation;

public static class GnouGnouAnimationPlanner
{
    private static readonly IReadOnlyList<AnimationStation> DefaultStations =
    [
        new("station-ai", "Ideas & AI", AnimationStationKind.Ai, new AnimationPoint(225, 235)),
        new("station-mcp", "MCP Radio", AnimationStationKind.Mcp, new AnimationPoint(520, 185)),
        new("station-writing", "Writing Table", AnimationStationKind.Writing, new AnimationPoint(815, 190)),
        new("station-planning", "Planning Board", AnimationStationKind.Planning, new AnimationPoint(1110, 205)),
        new("station-human", "Human Counter", AnimationStationKind.Human, new AnimationPoint(1370, 300)),
        new("station-handoff", "Handoff Point", AnimationStationKind.Handoff, new AnimationPoint(1300, 600)),
        new("station-workbench", "Workbench", AnimationStationKind.Workbench, new AnimationPoint(340, 610))
    ];

    public static GnouGnouAnimationPlan Build(
        WorkflowPreviewDocument document,
        GnouGnouAnimationOptions? options = null)
    {
        return Build(WorkflowPreviewValidator.Validate(document), options);
    }

    public static GnouGnouAnimationPlan Build(
        WorkflowPreviewValidationResult validation,
        GnouGnouAnimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(validation);
        options ??= new GnouGnouAnimationOptions();
        ValidateOptions(options);
        if (!validation.IsValid)
            throw new GnouGnouAnimationPlanException("INVALID_PREVIEW", "The preview workflow contains validation errors.");

        var entrypoint = validation.Entrypoint
            ?? throw new GnouGnouAnimationPlanException("INVALID_ENTRYPOINT", "The preview does not have an entrypoint.");
        var context = new PlanContext(validation.Document, options, validation.Diagnostics);
        return context.Build(entrypoint);
    }

    /// <summary>
    /// Builds the deterministic scene graph used by a live execution without
    /// exposing the autonomous preview schedule to the consumer.
    /// </summary>
    public static GnouGnouAnimationPlan BuildLive(
        WorkflowPreviewDocument document,
        GnouGnouAnimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return BuildLive(WorkflowPreviewValidator.Validate(document), options);
    }

    /// <summary>
    /// Builds the deterministic scene graph used by a live execution without
    /// exposing the autonomous preview schedule to the consumer.
    /// </summary>
    public static GnouGnouAnimationPlan BuildLive(
        WorkflowPreviewValidationResult validation,
        GnouGnouAnimationOptions? options = null)
    {
        var scheduled = Build(validation, options);
        return new GnouGnouAnimationPlan
        {
            Seed = scheduled.Seed,
            Scene = scheduled.Scene,
            Entrypoint = scheduled.Entrypoint,
            Actors = scheduled.Actors,
            Stations = scheduled.Stations,
            Lanes = scheduled.Lanes,
            Nodes = scheduled.Nodes,
            Edges = scheduled.Edges,
            Bounds = scheduled.Bounds,
            Tasks = scheduled.Tasks,
            Events = [],
            Warnings = scheduled.Warnings,
            DurationMs = 0
        };
    }

    private static void ValidateOptions(GnouGnouAnimationOptions options)
    {
        if (options.MoveDurationMs <= 0 || options.WorkDurationMs <= 0 || options.HandoffDurationMs <= 0 || options.EffectDurationMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Animation durations must be positive.");
        if (options.MaxStepOccurrences <= 0 || options.MaxWorkflowActors <= 0 || options.MaxClonesPerFork <= 0 || options.MaxPreviewLoopIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Animation limits must be positive.");
    }

    private sealed class PlanContext
    {
        private readonly WorkflowPreviewDocument _document;
        private readonly GnouGnouAnimationOptions _options;
        private readonly List<AnimationActor> _actors = [];
        private readonly List<AnimationTaskObject> _tasks = [];
        private readonly List<SimulationEvent> _events = [];
        private readonly List<WorkflowPreviewDiagnostic> _warnings;
        private StableRandom _random;
        private int _actorCounter;
        private int _cloneCounter;
        private int _stepOccurrences;
        private bool _failureMatched;

        public PlanContext(
            WorkflowPreviewDocument document,
            GnouGnouAnimationOptions options,
            IReadOnlyList<WorkflowPreviewDiagnostic> diagnostics)
        {
            _document = document;
            _options = options;
            _warnings = diagnostics.Where(static diagnostic => diagnostic.Severity == WorkflowPreviewDiagnosticSeverity.Warning).ToList();
            _random = new StableRandom(options.Seed);
        }

        public GnouGnouAnimationPlan Build(string entrypoint)
        {
            var scene = ResolveScene(_options.Scene);
            var masterActor = new AnimationActor(
                "actor-master",
                entrypoint,
                $"Master · {entrypoint}",
                AnimationActorKind.Master,
                _options.Seed,
                new AnimationPoint(780, 690),
                InitiallyVisible: true);
            _actors.Add(masterActor);
            _actorCounter++;
            const string rootTaskId = "task-root";
            _tasks.Add(new AnimationTaskObject(rootTaskId, "GnOuGo project parcel", "project-parcel", masterActor.Id, InitiallyVisible: true));
            var master = new ActorRuntime(masterActor, rootTaskId, masterActor.Home);

            Add(SimulationEventTypes.SimulationStarted, 0, workflowName: entrypoint, message: "Preparing the autonomous workflow preview.");
            Add(SimulationEventTypes.ActorSpawned, 0, _options.EffectDurationMs, master, message: "The bearded master is ready.");
            Add(SimulationEventTypes.TaskDropped, 0, _options.EffectDurationMs, master, taskId: rootTaskId,
                x: master.Position.X, y: master.Position.Y - 95, message: "Input arrives from the sky.");
            long cursor = _options.EffectDurationMs;
            Add(SimulationEventTypes.TaskPickedUp, cursor, _options.HandoffDurationMs, master, taskId: rootTaskId,
                message: "The master receives the input task.");
            Add(SimulationEventTypes.WorkflowStarted, cursor, actor: master, workflowName: entrypoint,
                workflowInstanceId: "workflow-master", message: $"Workflow '{entrypoint}' starts.");
            cursor += _options.HandoffDurationMs;

            var state = new ExecutionState();
            var result = PlanSteps(_document.Workflows[entrypoint].Steps, entrypoint, "workflow-master", master, cursor, state);
            cursor = result.EndMs;

            Add(SimulationEventTypes.ActorMoved, cursor, _options.MoveDurationMs, master,
                workflowName: entrypoint, workflowInstanceId: "workflow-master",
                nodeId: "@finish", taskId: rootTaskId,
                message: "The master carries the project parcel to the workflow finish.");
            cursor += _options.MoveDurationMs;
            Add(SimulationEventTypes.WorkflowCompleted, cursor, actor: master, workflowName: entrypoint,
                workflowInstanceId: "workflow-master", status: result.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: result.Failed ? "The master receives a failed task." : "The master receives the completed task.");
            Add(SimulationEventTypes.ActorMoved, cursor, _options.MoveDurationMs, master,
                workflowName: entrypoint, workflowInstanceId: "workflow-master",
                nodeId: "@delivery", taskId: rootTaskId,
                message: "The master carries the finished project parcel to the delivery dock.");
            cursor += _options.MoveDurationMs;
            Add(SimulationEventTypes.OutputSent, cursor, _options.EffectDurationMs, master,
                workflowName: entrypoint, workflowInstanceId: "workflow-master",
                nodeId: "@delivery", taskId: rootTaskId,
                status: result.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: "The master launches the finished GnOuGo project parcel into the sky.");
            cursor += _options.EffectDurationMs;
            Add(SimulationEventTypes.SimulationCompleted, cursor, actor: master,
                status: result.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: result.Failed ? "Synthetic simulation failed as requested." : "Synthetic simulation completed.");

            var layout = WorkflowSceneLayoutBuilder.Build(_document, entrypoint, _actors, _events);
            var resolvedActors = ResolveActors(layout);
            var ordered = ApplyProgress(_events
                .OrderBy(static item => item.OffsetMs)
                .ThenBy(static item => item.Sequence)
                .Select(item => ResolveEvent(item, layout))
                .Select(static (item, index) => item with { Sequence = index })
                .ToArray());
            var duration = Math.Max(cursor, ordered.Length == 0 ? 0 : ordered.Max(static item => item.OffsetMs + item.DurationMs));
            return new GnouGnouAnimationPlan
            {
                Seed = _options.Seed,
                Scene = scene,
                Entrypoint = entrypoint,
                Actors = resolvedActors,
                Stations = layout.Stations,
                Lanes = layout.Lanes,
                Nodes = layout.Nodes,
                Edges = layout.Edges,
                Bounds = layout.Bounds,
                Tasks = _tasks.ToArray(),
                Events = ordered,
                Warnings = _warnings.ToArray(),
                DurationMs = duration
            };
        }

        private AnimationActor[] ResolveActors(WorkflowSceneLayout layout)
        {
            var result = new AnimationActor[_actors.Count];
            for (var index = 0; index < _actors.Count; index++)
            {
                var actor = _actors[index];
                AnimationPoint home;
                if (layout.ActorHomes.TryGetValue(actor.Id, out var resolved))
                {
                    home = resolved;
                }
                else if (actor.CloneOfActorId is not null
                         && layout.ActorHomes.TryGetValue(actor.CloneOfActorId, out var parentHome))
                {
                    home = parentHome;
                }
                else
                {
                    home = actor.Home;
                }
                result[index] = actor with { Home = home };
            }
            return result;
        }

        private SimulationEvent ResolveEvent(SimulationEvent item, WorkflowSceneLayout layout)
        {
            var nodeId = ResolveNodeId(item, layout);
            var targetNodeId = ResolveTargetNodeId(item, layout);
            var stationId = item.StationId;
            AnimationPoint? point = null;

            if (nodeId is not null && layout.NodesById.TryGetValue(nodeId, out var node))
            {
                stationId = node.StationId;
                point = layout.ActorPointForNode(nodeId);
            }

            if (item.Type == SimulationEventTypes.ActorSpawned
                && item.ActorId is not null
                && layout.ActorHomes.TryGetValue(item.ActorId, out var home))
                point = home;

            if (item.Type == SimulationEventTypes.TaskDropped
                && item.ActorId is not null
                && layout.ActorHomes.TryGetValue(item.ActorId, out var taskHome))
                point = new AnimationPoint(taskHome.X, taskHome.Y - 105);

            var x = item.X;
            var y = item.Y;
            if (point is not null
                && item.Type is SimulationEventTypes.ActorMoved
                    or SimulationEventTypes.ActorWaiting
                    or SimulationEventTypes.ActorSpawned
                    or SimulationEventTypes.ActorMerged
                    or SimulationEventTypes.TaskDropped)
            {
                x = point.Value.X;
                y = point.Value.Y;
            }

            if (item.Type == SimulationEventTypes.OutputSent
                && nodeId is not null
                && layout.NodesById.TryGetValue(nodeId, out var delivery))
            {
                x = delivery.Position.X;
                y = -90;
            }

            var edgeId = item.EdgeId;
            if (edgeId is null && nodeId is not null)
            {
                edgeId = targetNodeId is not null
                    ? layout.Edges.FirstOrDefault(edge =>
                        string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal)
                        && string.Equals(edge.ToNodeId, targetNodeId, StringComparison.Ordinal))?.Id
                    : layout.Edges.FirstOrDefault(edge =>
                        string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal))?.Id;
            }

            return item with
            {
                NodeId = nodeId,
                TargetNodeId = targetNodeId,
                EdgeId = edgeId,
                StationId = stationId,
                X = x,
                Y = y
            };
        }

        private string? ResolveNodeId(SimulationEvent item, WorkflowSceneLayout layout)
        {
            if (string.Equals(item.NodeId, "@delivery", StringComparison.Ordinal)
                || item.Type is SimulationEventTypes.OutputSent or SimulationEventTypes.SimulationCompleted)
            {
                return item.WorkflowInstanceId is not null
                       && layout.DeliveryNodes.TryGetValue(item.WorkflowInstanceId, out var delivery)
                    ? delivery
                    : layout.DeliveryNodes.Values.FirstOrDefault();
            }

            if (string.Equals(item.NodeId, "@return", StringComparison.Ordinal))
                return layout.FindReturnNode(item.WorkflowInstanceId, item.StepId);

            if (string.Equals(item.NodeId, "@finish", StringComparison.Ordinal)
                && item.WorkflowInstanceId is not null
                && layout.FinishNodes.TryGetValue(item.WorkflowInstanceId, out var explicitFinish))
                return explicitFinish;

            if (string.Equals(item.NodeId, "@child-finish", StringComparison.Ordinal)
                && item.ActorId is not null
                && layout.ActorInstances.TryGetValue(item.ActorId, out var childInstance)
                && layout.FinishNodes.TryGetValue(childInstance, out var childFinish))
                return childFinish;

            if (string.Equals(item.NodeId, "@dynamic-work", StringComparison.Ordinal)
                && item.WorkflowInstanceId is not null)
            {
                return layout.Nodes.FirstOrDefault(node =>
                    string.Equals(node.WorkflowInstanceId, item.WorkflowInstanceId, StringComparison.Ordinal)
                    && string.Equals(node.StepId, "dynamic-work", StringComparison.Ordinal))?.Id;
            }

            if (item.Type is SimulationEventTypes.ActorMerged
                or SimulationEventTypes.TaskMerged
                or SimulationEventTypes.ParallelCompleted)
            {
                return layout.FindJoinNode(item.WorkflowInstanceId, item.StepId)
                       ?? layout.FindStepNode(item.WorkflowInstanceId, item.StepId);
            }

            if (item.Type == SimulationEventTypes.WorkflowStarted
                && item.WorkflowInstanceId is not null)
                return $"node-{Slug(item.WorkflowInstanceId)}-start";

            if (item.Type == SimulationEventTypes.WorkflowCompleted
                && item.WorkflowInstanceId is not null
                && layout.FinishNodes.TryGetValue(item.WorkflowInstanceId, out var finish))
                return finish;

            return item.NodeId is null
                ? layout.FindStepNode(item.WorkflowInstanceId, item.StepId)
                : item.NodeId;
        }

        private string? ResolveTargetNodeId(SimulationEvent item, WorkflowSceneLayout layout)
        {
            if (string.Equals(item.TargetNodeId, "@return", StringComparison.Ordinal))
                return layout.FindReturnNode(item.WorkflowInstanceId, item.StepId);

            if (item.TargetNodeId is not null)
                return item.TargetNodeId;

            if (item.Type == SimulationEventTypes.TaskHandedOff
                && item.TargetActorId is not null
                && layout.ActorInstances.TryGetValue(item.TargetActorId, out var targetInstance))
            {
                var startId = $"node-{Slug(targetInstance)}-start";
                if (layout.NodesById.ContainsKey(startId))
                    return startId;
            }

            if (item.Type is SimulationEventTypes.ActorMerged or SimulationEventTypes.TaskMerged)
                return layout.FindJoinNode(item.WorkflowInstanceId, item.StepId);

            return null;
        }

        private static SimulationEvent[] ApplyProgress(SimulationEvent[] events)
        {
            var progressEvents = events
                .Where(static item =>
                    item.Type == SimulationEventTypes.StepCompleted
                    && IsProgressStep(item.StepType))
                .ToArray();
            var total = progressEvents.Length;
            var current = 0;
            var result = new SimulationEvent[events.Length];
            for (var index = 0; index < events.Length; index++)
            {
                var item = events[index];
                if (item.Type == SimulationEventTypes.StepCompleted && IsProgressStep(item.StepType))
                {
                    if (item.Status == SimulationStatus.Succeeded)
                        current++;
                    item = item with { ProgressCurrent = current, ProgressTotal = total };
                }
                result[index] = item;
            }
            return result;
        }

        private static bool IsProgressStep(string? stepType) =>
            WorkflowVisualFilter.IsLongRunningStepType(stepType);

        private BranchResult PlanSteps(
            IReadOnlyList<WorkflowPreviewStep> steps,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs,
            ExecutionState state)
        {
            var cursor = startMs;
            foreach (var step in steps)
            {
                if (state.Failed)
                {
                    EmitSkippedTree(step, workflowName, workflowInstanceId, actor, cursor);
                    continue;
                }

                var result = PlanStep(step, workflowName, workflowInstanceId, actor, cursor, state);
                cursor = result.EndMs;
                state.Failed |= result.Failed;
            }
            return new BranchResult(cursor, state.Failed);
        }

        private BranchResult PlanStep(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs,
            ExecutionState state)
        {
            RegisterStep();
            if (!ShouldRun(step, workflowName, workflowInstanceId, actor, startMs))
            {
                Add(SimulationEventTypes.StepSkipped, startMs, actor: actor, workflowName: workflowName,
                    workflowInstanceId: workflowInstanceId, step: step, status: SimulationStatus.Skipped,
                    message: $"Step '{step.Id}' is skipped in this seeded preview.");
                return new BranchResult(startMs, false);
            }

            return step.Type switch
            {
                "sequence" => PlanSequence(step, workflowName, workflowInstanceId, actor, startMs, state),
                "parallel" => PlanParallel(step, workflowName, workflowInstanceId, actor, startMs),
                "loop.sequential" => PlanSequentialLoop(step, workflowName, workflowInstanceId, actor, startMs, state),
                "loop.parallel" => PlanParallelLoop(step, workflowName, workflowInstanceId, actor, startMs),
                "switch" => PlanSwitch(step, workflowName, workflowInstanceId, actor, startMs, state),
                "workflow.call" => PlanWorkflowCall(step, workflowName, workflowInstanceId, actor, startMs),
                "workflow.route" or "workflow.execute" => PlanDynamicCall(step, workflowName, workflowInstanceId, actor, startMs),
                _ => PlanAtomic(step, workflowName, workflowInstanceId, actor, startMs)
            };
        }

        private BranchResult PlanSequence(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs,
            ExecutionState state)
        {
            Add(SimulationEventTypes.StepStarted, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, status: SimulationStatus.Running,
                message: $"Sequence '{step.Id}' starts.");
            var result = PlanSteps(step.Steps ?? [], workflowName, workflowInstanceId, actor, startMs, state);
            Add(SimulationEventTypes.StepCompleted, result.EndMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step,
                status: result.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Sequence '{step.Id}' {(result.Failed ? "failed" : "completed")}.");
            return result;
        }

        private BranchResult PlanParallel(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            var branches = step.Branches ?? [];
            return PlanParallelBranches(step, branches.Select(static branch => branch.Steps).ToArray(), workflowName,
                workflowInstanceId, actor, startMs);
        }

        private BranchResult PlanParallelBranches(
            WorkflowPreviewStep owner,
            IReadOnlyList<List<WorkflowPreviewStep>> branches,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            var visibleBranches = branches
                .Select((steps, index) => (Steps: steps, Index: index))
                .Where(branch => WorkflowVisualFilter.StepsContainVisibleWork(_document, branch.Steps))
                .ToArray();
            if (visibleBranches.Length == 0)
                return PlanHiddenParallelBranches(owner, branches, workflowName, workflowInstanceId, actor, startMs);

            if (visibleBranches.Length > _options.MaxClonesPerFork)
                throw new GnouGnouAnimationPlanException("CLONE_LIMIT", $"Parallel step '{owner.Id}' requires {visibleBranches.Length} visible clones; the limit is {_options.MaxClonesPerFork}.");

            var planning = Station(AnimationStationKind.Planning);
            AddMove(actor, planning, startMs, workflowName, workflowInstanceId, owner);
            var forkAt = startMs + _options.MoveDurationMs;
            Add(SimulationEventTypes.StepStarted, forkAt, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: owner, status: SimulationStatus.Running,
                message: $"Parallel step '{owner.Id}' starts.");
            Add(SimulationEventTypes.ParallelStarted, forkAt, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: owner, message: "Matrix duplication begins.");

            var branchStart = forkAt + _options.EffectDurationMs;
            var results = new List<BranchResult>(branches.Count);
            var clones = new List<ActorRuntime>(visibleBranches.Length);
            foreach (var branch in visibleBranches)
            {
                var clone = CreateClone(actor, workflowName, branch.Index);
                clones.Add(clone);
                Add(SimulationEventTypes.ActorCloned, forkAt, _options.EffectDurationMs, actor,
                    targetActorId: clone.Actor.Id, branchId: $"branch-{branch.Index + 1}",
                    message: $"Matrix clone {branch.Index + 1} materializes.");
                Add(SimulationEventTypes.TaskCloned, forkAt, _options.EffectDurationMs, clone,
                    taskId: clone.TaskId, branchId: $"branch-{branch.Index + 1}", message: "The task is duplicated for parallel work.");
                var branchState = new ExecutionState();
                results.Add(PlanSteps(branch.Steps, workflowName, workflowInstanceId, clone, branchStart, branchState));
            }

            foreach (var hiddenBranch in branches.Where(branch =>
                         !WorkflowVisualFilter.StepsContainVisibleWork(_document, branch)))
            {
                var hiddenState = new ExecutionState();
                results.Add(PlanSteps(hiddenBranch, workflowName, workflowInstanceId, actor, branchStart, hiddenState));
            }

            var mergeAt = results.Count == 0 ? branchStart : results.Max(static result => result.EndMs);
            var failed = results.Any(static result => result.Failed);
            foreach (var clone in clones)
            {
                Add(SimulationEventTypes.TaskMerged, mergeAt, _options.EffectDurationMs, clone,
                    targetActorId: actor.Actor.Id, taskId: clone.TaskId, message: "Parallel task results recombine.");
                Add(SimulationEventTypes.ActorMerged, mergeAt, _options.EffectDurationMs, clone,
                    targetActorId: actor.Actor.Id, x: planning.Position.X, y: planning.Position.Y,
                    message: "The matrix clone merges back into its GnOuGo.");
            }
            var end = mergeAt + _options.EffectDurationMs;
            Add(SimulationEventTypes.ParallelCompleted, end, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: owner,
                status: failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: "Parallel branches join.");
            Add(SimulationEventTypes.StepCompleted, end, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: owner,
                status: failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Parallel step '{owner.Id}' {(failed ? "failed" : "completed")}.");
            actor.Position = planning.Position;
            return new BranchResult(end, failed);
        }

        private BranchResult PlanHiddenParallelBranches(
            WorkflowPreviewStep owner,
            IReadOnlyList<List<WorkflowPreviewStep>> branches,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            Add(SimulationEventTypes.StepStarted, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: owner, status: SimulationStatus.Running,
                message: $"Transition '{owner.Id}' is collapsed because it contains no long-running LLM or MCP task.");
            var results = new List<BranchResult>(branches.Count);
            foreach (var branch in branches)
            {
                var branchState = new ExecutionState();
                results.Add(PlanSteps(branch, workflowName, workflowInstanceId, actor, startMs, branchState));
            }

            var end = results.Count == 0 ? startMs : results.Max(static result => result.EndMs);
            var failed = results.Any(static result => result.Failed);
            Add(SimulationEventTypes.StepCompleted, end, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: owner,
                status: failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Collapsed transition '{owner.Id}' {(failed ? "failed" : "completed")}.");
            return new BranchResult(end, failed);
        }

        private BranchResult PlanSequentialLoop(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs,
            ExecutionState state)
        {
            Add(SimulationEventTypes.StepStarted, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, status: SimulationStatus.Running,
                message: $"Sequential loop '{step.Id}' starts.");
            var count = ResolveLoopCount(step, workflowName);
            var cursor = startMs;
            for (var index = 0; index < count && !state.Failed; index++)
            {
                Add(SimulationEventTypes.DecisionSimulated, cursor, actor: actor, workflowName: workflowName,
                    workflowInstanceId: workflowInstanceId, step: step, message: $"Loop iteration {index + 1} of {count}.");
                var result = PlanSteps(step.Steps ?? [], workflowName, workflowInstanceId, actor, cursor, state);
                cursor = result.EndMs;
            }
            Add(SimulationEventTypes.StepCompleted, cursor, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step,
                status: state.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Sequential loop '{step.Id}' {(state.Failed ? "failed" : "completed")}.");
            return new BranchResult(cursor, state.Failed);
        }

        private BranchResult PlanParallelLoop(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            var count = ResolveLoopCount(step, workflowName);
            var branches = Enumerable.Range(0, count)
                .Select(_ => step.Steps ?? [])
                .ToArray();
            return PlanParallelBranches(step, branches, workflowName, workflowInstanceId, actor, startMs);
        }

        private BranchResult PlanSwitch(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs,
            ExecutionState state)
        {
            Add(SimulationEventTypes.StepStarted, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, status: SimulationStatus.Running,
                message: $"Switch '{step.Id}' evaluates a preview path.");
            var choices = new List<(string Label, List<WorkflowPreviewStep> Steps)>();
            if (step.Cases is { } cases)
                choices.AddRange(cases.Select((item, index) => (item.Value ?? item.When ?? $"case {index + 1}", item.Steps)));
            if (step.Default is { Count: > 0 } defaultSteps)
                choices.Add(("default", defaultSteps));

            if (choices.Count == 0)
                return new BranchResult(startMs, false);

            var selected = TryResolveSwitchValue(step, out var value)
                ? choices.FindIndex(choice => string.Equals(choice.Label, value, StringComparison.OrdinalIgnoreCase))
                : -1;
            var simulated = selected < 0;
            if (simulated)
                selected = _random.Next(choices.Count);
            var choice = choices[selected];
            if (simulated)
                AddWarning("SIMULATED_SWITCH", $"Switch '{step.Id}' selected '{choice.Label}' from the seed because runtime data is unavailable.", workflowName, step.Id);
            Add(SimulationEventTypes.DecisionSimulated, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, branchId: choice.Label,
                message: $"Preview selects '{choice.Label}'.");
            var result = PlanSteps(choice.Steps, workflowName, workflowInstanceId, actor, startMs, state);
            Add(SimulationEventTypes.StepCompleted, result.EndMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step,
                status: result.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Switch '{step.Id}' {(result.Failed ? "failed" : "completed")}.");
            return result;
        }

        private BranchResult PlanWorkflowCall(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            if (!WorkflowPreviewValidator.TryGetLocalWorkflowName(step, out var target)
                || !_document.Workflows.TryGetValue(target, out var childWorkflow))
                return PlanDynamicCall(step, workflowName, workflowInstanceId, actor, startMs);

            if (!WorkflowVisualFilter.StepsContainVisibleWork(_document, childWorkflow.Steps))
                return PlanHiddenWorkflowCall(step, workflowName, workflowInstanceId, actor, startMs, target, childWorkflow);

            var handoff = Station(AnimationStationKind.Handoff);
            AddMove(actor, handoff, startMs, workflowName, workflowInstanceId, step);
            var cursor = startMs + _options.MoveDurationMs;
            Add(SimulationEventTypes.StepStarted, cursor, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId, status: SimulationStatus.Running,
                message: $"Calling child workflow '{target}'.");
            if (ShouldFail(workflowName, step))
            {
                cursor += _options.WorkDurationMs;
                Add(SimulationEventTypes.StepCompleted, cursor, actor: actor, workflowName: workflowName,
                    workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId, status: SimulationStatus.Failed,
                    message: $"Synthetic failure injected at '{step.Id}'.");
                return new BranchResult(cursor, true);
            }

            var child = CreateWorker(target, handoff.Position, actor.TaskId);
            Add(SimulationEventTypes.ActorSpawned, cursor, _options.EffectDurationMs, child,
                workflowName: workflowName, workflowInstanceId: workflowInstanceId, step: step,
                message: $"Subordinate '{target}' joins its workflow lane.");
            cursor += _options.EffectDurationMs;
            Add(SimulationEventTypes.TaskHandedOff, cursor, _options.HandoffDurationMs, actor,
                targetActorId: child.Actor.Id, workflowName: workflowName, workflowInstanceId: workflowInstanceId,
                step: step, taskId: actor.TaskId, message: $"The project parcel is handed to '{target}'.");
            cursor += _options.HandoffDurationMs;
            var childInstanceId = $"workflow-{child.Actor.Id}";
            var childStartedAt = cursor;
            Add(SimulationEventTypes.WorkflowStarted, cursor, actor: child, workflowName: target,
                workflowInstanceId: childInstanceId, message: $"Child workflow '{target}' starts.");
            var childState = new ExecutionState();
            var childResult = PlanSteps(childWorkflow.Steps, target, childInstanceId, child, cursor, childState);
            cursor = childResult.EndMs;
            Add(SimulationEventTypes.ActorMoved, cursor, _options.MoveDurationMs, child,
                workflowName: target, workflowInstanceId: childInstanceId,
                nodeId: "@finish", taskId: child.TaskId,
                message: $"'{target}' carries the project parcel to its workflow finish.");
            cursor += _options.MoveDurationMs;
            Add(SimulationEventTypes.ActorMoved, childStartedAt, _options.MoveDurationMs, actor,
                workflowName: workflowName, workflowInstanceId: workflowInstanceId, step: step,
                nodeId: "@return", message: $"{actor.Actor.Label} walks down to the return point while '{target}' works.");
            var waitingAt = childStartedAt + _options.MoveDurationMs;
            if (cursor > waitingAt)
            {
                Add(SimulationEventTypes.ActorWaiting, waitingAt, cursor - waitingAt, actor,
                    workflowName: workflowName, workflowInstanceId: workflowInstanceId, step: step,
                    nodeId: "@return", message: $"{actor.Actor.Label} waits for '{target}' to return the parcel.");
            }
            Add(SimulationEventTypes.WorkflowCompleted, cursor, actor: child, workflowName: target,
                workflowInstanceId: childInstanceId,
                status: childResult.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Child workflow '{target}' {(childResult.Failed ? "failed" : "completed")}.");
            Add(SimulationEventTypes.TaskHandedOff, cursor, _options.HandoffDurationMs, child,
                targetActorId: actor.Actor.Id, workflowName: workflowName, workflowInstanceId: workflowInstanceId,
                step: step, nodeId: "@child-finish", targetNodeId: "@return", taskId: actor.TaskId,
                status: childResult.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"'{target}' returns the {(childResult.Failed ? "failed " : "")}project parcel.");
            cursor += _options.HandoffDurationMs;
            Add(SimulationEventTypes.StepCompleted, cursor, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step,
                taskId: actor.TaskId,
                status: childResult.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Workflow call '{step.Id}' {(childResult.Failed ? "failed" : "completed")}.");
            actor.Position = handoff.Position;
            return new BranchResult(cursor, childResult.Failed);
        }

        private BranchResult PlanHiddenWorkflowCall(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs,
            string target,
            WorkflowPreviewDefinition childWorkflow)
        {
            Add(SimulationEventTypes.StepStarted, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId,
                status: SimulationStatus.Running,
                message: $"Workflow transition '{step.Id}' is collapsed because '{target}' contains no long-running LLM or MCP task.");
            if (ShouldFail(workflowName, step))
            {
                var failedAt = startMs + HiddenStepDurationMs;
                Add(SimulationEventTypes.StepCompleted, failedAt, actor: actor, workflowName: workflowName,
                    workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId,
                    status: SimulationStatus.Failed,
                    message: $"Synthetic failure injected at '{step.Id}'.");
                return new BranchResult(failedAt, true);
            }

            var childState = new ExecutionState();
            var childInstanceId = $"{workflowInstanceId}-collapsed-{Slug(step.Id)}";
            var childResult = PlanSteps(
                childWorkflow.Steps,
                target,
                childInstanceId,
                actor,
                startMs + HiddenStepDurationMs,
                childState);
            Add(SimulationEventTypes.StepCompleted, childResult.EndMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId,
                status: childResult.Failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: $"Collapsed workflow call '{step.Id}' {(childResult.Failed ? "failed" : "completed")}.");
            return childResult;
        }

        private BranchResult PlanDynamicCall(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            Add(SimulationEventTypes.StepStarted, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId, status: SimulationStatus.Running,
                message: $"Dynamic workflow transition '{step.Id}' is collapsed in this visual preview.");
            var cursor = startMs + HiddenStepDurationMs;
            if (ShouldFail(workflowName, step))
            {
                Add(SimulationEventTypes.StepCompleted, cursor, actor: actor, workflowName: workflowName,
                    workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId, status: SimulationStatus.Failed,
                    message: $"Synthetic failure injected at '{step.Id}'.");
                return new BranchResult(cursor, true);
            }

            AddWarning("SIMULATED_DYNAMIC_CALL", $"Dynamic workflow transition '{step.Id}' is recorded without a visual subordinate because its runtime work is unknown.", workflowName, step.Id);
            Add(SimulationEventTypes.StepCompleted, cursor, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId, status: SimulationStatus.Succeeded,
                message: $"Dynamic transition '{step.Id}' completed synthetically without a visual workstation.");
            return new BranchResult(cursor, false);
        }

        private BranchResult PlanAtomic(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            if (!WorkflowVisualFilter.IsLongRunningStepType(step.Type))
                return PlanHiddenAtomic(step, workflowName, workflowInstanceId, actor, startMs);

            var station = StationFor(step.Type);
            AddMove(actor, station, startMs, workflowName, workflowInstanceId, step);
            var workAt = startMs + _options.MoveDurationMs;
            var (workDuration, workPace) = WorkProfile(step);
            Add(SimulationEventTypes.StepStarted, workAt, workDuration, actor,
                workflowName: workflowName, workflowInstanceId: workflowInstanceId, step: step,
                stationId: station.Id, taskId: actor.TaskId, status: SimulationStatus.Running,
                message: $"{actor.Actor.Label} begins a {workPace} pass on the '{step.Id}' laptop.");
            var end = workAt + workDuration;
            var failed = ShouldFail(workflowName, step);
            Add(SimulationEventTypes.StepCompleted, end, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, stationId: station.Id, taskId: actor.TaskId,
                status: failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: failed ? $"Synthetic failure injected at '{step.Id}'." : $"Step '{step.Id}' completed synthetically.");
            actor.Position = station.Position;
            return new BranchResult(end, failed);
        }

        private BranchResult PlanHiddenAtomic(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            Add(SimulationEventTypes.StepStarted, startMs, HiddenStepDurationMs, actor,
                workflowName: workflowName, workflowInstanceId: workflowInstanceId, step: step,
                taskId: actor.TaskId, status: SimulationStatus.Running,
                message: $"Short step '{step.Id}' runs without a visual workstation.");
            var end = startMs + HiddenStepDurationMs;
            var failed = ShouldFail(workflowName, step);
            Add(SimulationEventTypes.StepCompleted, end, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, taskId: actor.TaskId,
                status: failed ? SimulationStatus.Failed : SimulationStatus.Succeeded,
                message: failed
                    ? $"Synthetic failure injected at '{step.Id}'."
                    : $"Short step '{step.Id}' completed without interrupting the visual journey.");
            return new BranchResult(end, failed);
        }

        private (int DurationMs, string Pace) WorkProfile(WorkflowPreviewStep step)
        {
            var bucket = StableWorkBucket(step.Id);
            var factor = step.Type.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)
                ? .72 + bucket * .14
                : 1.32 + bucket * .2;
            var duration = Math.Max(260, (int)Math.Round(_options.WorkDurationMs * factor, MidpointRounding.AwayFromZero));
            var pace = factor switch
            {
                <= .9 => "quick",
                >= 1.52 => "deep-focus",
                _ => "steady"
            };
            return (duration, pace);
        }

        private int HiddenStepDurationMs => Math.Max(80, _options.WorkDurationMs / 8);

        private int StableWorkBucket(string stepId)
        {
            var hash = unchecked((uint)_options.Seed);
            foreach (var character in stepId)
                hash = unchecked(hash * 16777619u ^ character);
            return (int)(hash % 4);
        }

        private bool ShouldRun(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long startMs)
        {
            if (string.IsNullOrWhiteSpace(step.If))
                return true;
            if (bool.TryParse(step.If.Trim().Trim('$', '{', '}'), out var literal))
                return literal;

            var selected = _random.NextBool();
            AddWarning("SIMULATED_GUARD", $"Guard on step '{step.Id}' was resolved from the seed as {selected.ToString().ToLowerInvariant()}.", workflowName, step.Id);
            Add(SimulationEventTypes.DecisionSimulated, startMs, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, message: $"Seeded guard result: {selected.ToString().ToLowerInvariant()}.");
            return selected;
        }

        private int ResolveLoopCount(WorkflowPreviewStep step, string workflowName)
        {
            var count = TryReadCount(step.Input, out var resolved) ? resolved : 1;
            if (!TryReadCount(step.Input, out _))
                AddWarning("SIMULATED_LOOP", $"Loop '{step.Id}' has a runtime-dependent count and is previewed once.", workflowName, step.Id);
            if (count > _options.MaxPreviewLoopIterations)
            {
                AddWarning("LOOP_TRUNCATED", $"Loop '{step.Id}' is truncated from {count} to {_options.MaxPreviewLoopIterations} preview iterations.", workflowName, step.Id);
                count = _options.MaxPreviewLoopIterations;
            }
            return Math.Max(0, count);
        }

        private bool TryReadCount(JsonNode? inputNode, out int count)
        {
            count = 0;
            if (inputNode is not JsonObject input)
                return false;
            if (TryInt(input["times"], out count))
                return true;
            if (input["items"] is JsonArray items)
            {
                count = items.Count;
                return true;
            }
            if (TryExactInputReference(input["items"], out var resolved) && resolved is JsonArray resolvedItems)
            {
                count = resolvedItems.Count;
                return true;
            }
            return false;
        }

        private bool TryExactInputReference(JsonNode? node, out JsonNode? resolved)
        {
            resolved = null;
            if (node is not JsonValue value || !value.TryGetValue<string>(out var expression))
                return false;
            const string prefix = "${data.inputs.";
            if (!expression.StartsWith(prefix, StringComparison.Ordinal) || !expression.EndsWith('}'))
                return false;
            var path = expression[prefix.Length..^1].Split('.', StringSplitOptions.RemoveEmptyEntries);
            resolved = _options.Inputs;
            foreach (var segment in path)
            {
                if (resolved is not JsonObject obj || !obj.TryGetPropertyValue(segment, out resolved))
                    return false;
            }
            return true;
        }

        private bool TryResolveSwitchValue(WorkflowPreviewStep step, out string value)
        {
            value = "";
            if (!TryExactInputReference(JsonValue.Create(step.Expr), out var resolved) || resolved is not JsonValue scalar)
                return false;
            value = scalar.ToString();
            return true;
        }

        private static bool TryInt(JsonNode? node, out int result)
        {
            result = 0;
            if (node is not JsonValue value)
                return false;
            if (value.TryGetValue<int>(out result))
                return true;
            if (value.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue)
            {
                result = (int)longValue;
                return true;
            }
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private bool ShouldFail(string workflowName, WorkflowPreviewStep step)
        {
            if (_failureMatched || _options.FailAt is null)
                return false;
            if (!string.Equals(_options.FailAt.WorkflowName, workflowName, StringComparison.Ordinal)
                || !string.Equals(_options.FailAt.StepId, step.Id, StringComparison.Ordinal))
                return false;
            _failureMatched = true;
            return true;
        }

        private void EmitSkippedTree(
            WorkflowPreviewStep step,
            string workflowName,
            string workflowInstanceId,
            ActorRuntime actor,
            long offset)
        {
            Add(SimulationEventTypes.StepSkipped, offset, actor: actor, workflowName: workflowName,
                workflowInstanceId: workflowInstanceId, step: step, status: SimulationStatus.Skipped,
                message: $"Step '{step.Id}' is skipped after failure.");
            if (step.Steps is { } childSteps)
                foreach (var child in childSteps) EmitSkippedTree(child, workflowName, workflowInstanceId, actor, offset);
            if (step.Branches is { } branches)
                foreach (var branch in branches)
                    foreach (var child in branch.Steps) EmitSkippedTree(child, workflowName, workflowInstanceId, actor, offset);
            if (step.Cases is { } cases)
                foreach (var switchCase in cases)
                    foreach (var child in switchCase.Steps) EmitSkippedTree(child, workflowName, workflowInstanceId, actor, offset);
            if (step.Default is { } defaultSteps)
                foreach (var child in defaultSteps) EmitSkippedTree(child, workflowName, workflowInstanceId, actor, offset);
        }

        private ActorRuntime CreateWorker(string workflowName, AnimationPoint handoff, string taskId)
        {
            if (_actorCounter >= _options.MaxWorkflowActors)
                throw new GnouGnouAnimationPlanException("ACTOR_LIMIT", $"The simulation requires more than {_options.MaxWorkflowActors} workflow actors.");
            _actorCounter++;
            var id = $"actor-{_actorCounter}-{Slug(workflowName)}";
            var horizontalOffset = _actorCounter % 2 == 0 ? 145 : -145;
            var home = new AnimationPoint(
                Math.Clamp(handoff.X + horizontalOffset, 100, 1500),
                Math.Clamp(handoff.Y + 55, 160, 760));
            var actor = new AnimationActor(id, workflowName, workflowName, AnimationActorKind.Worker,
                unchecked(_options.Seed + _actorCounter * 7919), home);
            _actors.Add(actor);
            return new ActorRuntime(actor, taskId, home);
        }

        private ActorRuntime CreateClone(ActorRuntime parent, string workflowName, int branchIndex)
        {
            _cloneCounter++;
            var id = $"actor-clone-{_cloneCounter}";
            var actor = new AnimationActor(id, workflowName, $"Matrix {branchIndex + 1}", AnimationActorKind.Clone,
                parent.Actor.VisualSeed, parent.Position, parent.Actor.Id);
            _actors.Add(actor);
            var taskId = $"task-clone-{_cloneCounter}";
            _tasks.Add(new AnimationTaskObject(taskId, $"Branch {branchIndex + 1}", "branch", id));
            return new ActorRuntime(actor, taskId, parent.Position);
        }

        private void RegisterStep()
        {
            _stepOccurrences++;
            if (_stepOccurrences > _options.MaxStepOccurrences)
                throw new GnouGnouAnimationPlanException("STEP_LIMIT", $"The preview exceeds {_options.MaxStepOccurrences} simulated step occurrences.");
        }

        private void AddMove(
            ActorRuntime actor,
            AnimationStation station,
            long offset,
            string workflowName,
            string workflowInstanceId,
            WorkflowPreviewStep step)
        {
            Add(SimulationEventTypes.ActorMoved, offset, _options.MoveDurationMs, actor,
                workflowName: workflowName, workflowInstanceId: workflowInstanceId, step: step,
                stationId: station.Id, taskId: actor.TaskId, x: station.Position.X, y: station.Position.Y,
                message: $"{actor.Actor.Label} walks to {station.Label}.");
        }

        private AnimationStation StationFor(string stepType)
        {
            var kind = stepType switch
            {
                _ when stepType.StartsWith("llm.", StringComparison.Ordinal) => AnimationStationKind.Ai,
                _ when stepType.StartsWith("mcp.", StringComparison.Ordinal) => AnimationStationKind.Mcp,
                _ when stepType.StartsWith("template.", StringComparison.Ordinal) || stepType is "set" or "emit" => AnimationStationKind.Writing,
                _ when stepType.StartsWith("human.", StringComparison.Ordinal) => AnimationStationKind.Human,
                _ when stepType.StartsWith("workflow.", StringComparison.Ordinal) => AnimationStationKind.Handoff,
                _ => AnimationStationKind.Workbench
            };
            return Station(kind);
        }

        private static AnimationStation Station(AnimationStationKind kind) => DefaultStations.First(station => station.Kind == kind);

        private AnimationSceneKind ResolveScene(AnimationSceneKind scene)
        {
            if (scene != AnimationSceneKind.Random)
                return scene;
            return (AnimationSceneKind)(_random.Next(3) + 1);
        }

        private void AddWarning(string code, string message, string workflowName, string stepId) =>
            _warnings.Add(new WorkflowPreviewDiagnostic(code, message, WorkflowPreviewDiagnosticSeverity.Warning, workflowName, stepId));

        private void Add(
            string type,
            long offset,
            long duration = 0,
            ActorRuntime? actor = null,
            string? targetActorId = null,
            string? workflowName = null,
            string? workflowInstanceId = null,
            WorkflowPreviewStep? step = null,
            string? stationId = null,
            string? nodeId = null,
            string? targetNodeId = null,
            string? edgeId = null,
            string? taskId = null,
            string? branchId = null,
            SimulationStatus? status = null,
            double? x = null,
            double? y = null,
            string? message = null)
        {
            _events.Add(new SimulationEvent
            {
                Sequence = _events.Count,
                Type = type,
                OffsetMs = offset,
                DurationMs = duration,
                ActorId = actor?.Actor.Id,
                TargetActorId = targetActorId,
                WorkflowName = workflowName ?? actor?.Actor.WorkflowName,
                WorkflowInstanceId = workflowInstanceId,
                StepId = step?.Id,
                StepType = step?.Type,
                StationId = stationId,
                NodeId = nodeId,
                TargetNodeId = targetNodeId,
                EdgeId = edgeId,
                TaskId = taskId,
                BranchId = branchId,
                Status = status,
                X = x,
                Y = y,
                Message = message
            });
        }

        private static string Slug(string value)
        {
            var characters = value.ToLowerInvariant().Select(static character =>
                char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray();
            var slug = new string(characters).Trim('-');
            return string.IsNullOrEmpty(slug) ? "workflow" : slug;
        }
    }

    private sealed class ActorRuntime(AnimationActor actor, string taskId, AnimationPoint position)
    {
        public AnimationActor Actor { get; } = actor;
        public string TaskId { get; } = taskId;
        public AnimationPoint Position { get; set; } = position;
    }

    private sealed class ExecutionState
    {
        public bool Failed { get; set; }
    }

    private readonly record struct BranchResult(long EndMs, bool Failed);
}
