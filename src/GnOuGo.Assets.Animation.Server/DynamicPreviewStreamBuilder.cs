using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation.Server;

public sealed record SimulationStreamItem(
    long OffsetMs,
    SimulationEvent? Event = null,
    AnimationScenePatch? ScenePatch = null);

internal static class DynamicPreviewStreamBuilder
{
    private const string GeneratedWorkflowStepSource = """
        version: 1
        name: generated-preview
        entrypoint: generated
        workflows:
          generated:
            steps:
              - id: understand_request
                type: llm.call
              - id: perform_generated_work
                type: mcp.call
              - id: compose_result
                type: llm.call
        """;

    public static IReadOnlyList<SimulationStreamItem> Build(
        GnouGnouAnimationPlan plan,
        WorkflowPreviewDocument document,
        string sourceText,
        IReadOnlyList<SimulationEvent> scheduledEvents,
        double speed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scheduledEvents);

        var rootLane = plan.Lanes.FirstOrDefault(static lane => lane.IsEntrypoint);
        if (rootLane is null)
            return scheduledEvents
                .Select(static item => new SimulationStreamItem(item.OffsetMs, Event: item))
                .ToArray();

        var options = new GnouGnouAnimationOptions
        {
            Seed = plan.Seed,
            Scene = plan.Scene,
            MoveDurationMs = Scale(650, speed),
            WorkDurationMs = Scale(950, speed),
            HandoffDurationMs = Scale(600, speed),
            EffectDurationMs = Scale(500, speed)
        };
        var session = new WorkflowLiveAnimationSession(plan, options);
        foreach (var lane in plan.Lanes.OrderByDescending(static item => item.IsEntrypoint))
        {
            _ = session.Apply(new AnimationExecutionSignal
            {
                Kind = AnimationExecutionSignalKind.WorkflowStarted,
                WorkflowInstanceId = lane.WorkflowInstanceId,
                WorkflowName = lane.WorkflowName
            });
        }

        var result = new List<SimulationStreamItem>(scheduledEvents.Count + 32);
        long accumulatedDelay = 0;
        var dynamicSequence = 0;
        foreach (var original in scheduledEvents)
        {
            var adjusted = original with
            {
                OffsetMs = original.OffsetMs + accumulatedDelay,
                Message = DynamicTransitionMessage(original) ?? original.Message
            };
            result.Add(new SimulationStreamItem(adjusted.OffsetMs, Event: adjusted));
            if (original.Type != SimulationEventTypes.StepStarted
                || original.StepId is null
                || original.StepType is null
                || !IsDynamicExecution(original.StepType))
            {
                continue;
            }

            dynamicSequence++;
            var callerLane = plan.Lanes.FirstOrDefault(lane =>
                string.Equals(lane.WorkflowInstanceId, original.WorkflowInstanceId, StringComparison.Ordinal))
                ?? rootLane;
            var callerOccurrenceId = $"preview-dynamic-caller-{dynamicSequence}";
            _ = session.Apply(new AnimationExecutionSignal
            {
                Kind = AnimationExecutionSignalKind.StepStarted,
                WorkflowInstanceId = callerLane.WorkflowInstanceId,
                StepOccurrenceId = callerOccurrenceId,
                StepId = original.StepId,
                StepType = original.StepType
            });

            var child = ResolveChild(plan, document, sourceText, original.StepType, dynamicSequence);
            var childInstanceId = $"{callerLane.WorkflowInstanceId}-dynamic-{dynamicSequence}";
            var updates = new List<AnimationLiveUpdate>();
            updates.AddRange(session.Apply(new AnimationExecutionSignal
            {
                Kind = AnimationExecutionSignalKind.WorkflowStarted,
                WorkflowInstanceId = childInstanceId,
                ParentWorkflowInstanceId = callerLane.WorkflowInstanceId,
                CallerStepOccurrenceId = callerOccurrenceId,
                WorkflowName = child.Name,
                SourceText = child.SourceText
            }));

            var patch = updates.Select(static update => update.ScenePatch).FirstOrDefault(static item => item is not null);
            if (patch is null)
                continue;

            foreach (var station in patch.Stations)
            {
                var stepOccurrenceId = $"{childInstanceId}:{station.StepId}";
                updates.AddRange(session.Apply(new AnimationExecutionSignal
                {
                    Kind = AnimationExecutionSignalKind.StepStarted,
                    WorkflowInstanceId = childInstanceId,
                    StepOccurrenceId = stepOccurrenceId,
                    StepId = station.StepId,
                    StepType = station.StepType
                }));
                updates.AddRange(session.Apply(new AnimationExecutionSignal
                {
                    Kind = AnimationExecutionSignalKind.StepCompleted,
                    WorkflowInstanceId = childInstanceId,
                    StepOccurrenceId = stepOccurrenceId,
                    StepId = station.StepId,
                    StepType = station.StepType,
                    Status = SimulationStatus.Succeeded
                }));
            }

            updates.AddRange(session.Apply(new AnimationExecutionSignal
            {
                Kind = AnimationExecutionSignalKind.WorkflowCompleted,
                WorkflowInstanceId = childInstanceId,
                WorkflowName = child.Name,
                Status = SimulationStatus.Succeeded
            }));

            var cursor = adjusted.OffsetMs + Scale(100, speed);
            foreach (var update in updates)
            {
                if (update.ScenePatch is not null)
                {
                    result.Add(new SimulationStreamItem(cursor, ScenePatch: update.ScenePatch));
                    continue;
                }

                if (update.Event is null)
                    continue;
                var duration = PresentationDuration(update.Event, speed);
                var simulationEvent = update.Event with
                {
                    OffsetMs = cursor,
                    DurationMs = duration
                };
                result.Add(new SimulationStreamItem(cursor, Event: simulationEvent));
                cursor += PresentationGap(simulationEvent);
            }

            accumulatedDelay += Math.Max(
                Scale(500, speed),
                cursor - adjusted.OffsetMs + Scale(120, speed));
        }

        var eventSequence = 0;
        return result
            .OrderBy(static item => item.OffsetMs)
            .Select(item => item.Event is null
                ? item
                : item with { Event = item.Event with { Sequence = eventSequence++ } })
            .ToArray();
    }

    private static DynamicChild ResolveChild(
        GnouGnouAnimationPlan plan,
        WorkflowPreviewDocument document,
        string sourceText,
        string stepType,
        int ordinal)
    {
        if (stepType.Equals("workflow.route", StringComparison.OrdinalIgnoreCase))
        {
            var allocatedWorkflows = plan.Lanes
                .Select(static lane => lane.WorkflowName)
                .ToHashSet(StringComparer.Ordinal);
            var local = document.Workflows
                .Where(pair => !string.Equals(pair.Key, document.Entrypoint, StringComparison.Ordinal))
                .Where(pair => !allocatedWorkflows.Contains(pair.Key))
                .OrderByDescending(static pair => pair.Key.Contains("fallback", StringComparison.OrdinalIgnoreCase))
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(local.Key))
                return new DynamicChild(local.Key, sourceText);

            var name = $"routed-workflow-{ordinal}";
            return new DynamicChild(name, RoutedWorkflowSource(name));
        }

        return new DynamicChild($"generated-{ordinal}", GeneratedWorkflowStepSource);
    }

    private static string RoutedWorkflowSource(string workflowName) => $$"""
        version: 1
        name: {{workflowName}}
        entrypoint: {{workflowName}}
        workflows:
          {{workflowName}}:
            steps:
              - id: inspect_request
                type: llm.call
              - id: use_selected_capability
                type: mcp.call
              - id: return_routed_answer
                type: llm.call
        """;

    private static bool IsDynamicExecution(string stepType) =>
        stepType.Equals("workflow.route", StringComparison.OrdinalIgnoreCase)
        || stepType.Equals("workflow.execute", StringComparison.OrdinalIgnoreCase);

    private static string? DynamicTransitionMessage(SimulationEvent simulationEvent)
    {
        if (simulationEvent.StepType is null || !IsDynamicExecution(simulationEvent.StepType))
            return null;
        return simulationEvent.Type switch
        {
            SimulationEventTypes.StepStarted =>
                $"Dynamic transition '{simulationEvent.StepId}' is discovering its child workflow.",
            SimulationEventTypes.StepCompleted when simulationEvent.Status == SimulationStatus.Failed =>
                $"Dynamic transition '{simulationEvent.StepId}' failed.",
            SimulationEventTypes.StepCompleted =>
                $"Dynamic transition '{simulationEvent.StepId}' completed after the child workflow returned.",
            _ => null
        };
    }

    private static int PresentationDuration(SimulationEvent simulationEvent, double speed) =>
        simulationEvent.Type switch
        {
            SimulationEventTypes.ActorMoved => Scale(650, speed),
            SimulationEventTypes.ActorSpawned => Scale(520, speed),
            SimulationEventTypes.TaskHandedOff => Scale(650, speed),
            SimulationEventTypes.WorkflowDiscovered => Scale(500, speed),
            SimulationEventTypes.StepStarted when simulationEvent.StepType?.StartsWith("llm.", StringComparison.OrdinalIgnoreCase) == true =>
                Scale(1250, speed),
            SimulationEventTypes.StepStarted => Scale(850, speed),
            SimulationEventTypes.StepCompleted => Scale(360, speed),
            SimulationEventTypes.WorkflowCompleted => Scale(260, speed),
            _ => Math.Max(Scale(180, speed), (int)Math.Min(simulationEvent.DurationMs, 2_000))
        };

    private static int PresentationGap(SimulationEvent simulationEvent) =>
        simulationEvent.Type switch
        {
            SimulationEventTypes.ActorMoved
                or SimulationEventTypes.ActorSpawned
                or SimulationEventTypes.TaskHandedOff
                or SimulationEventTypes.StepStarted => (int)simulationEvent.DurationMs,
            SimulationEventTypes.WorkflowDiscovered => Math.Max(120, (int)simulationEvent.DurationMs / 2),
            SimulationEventTypes.StepCompleted => Math.Max(100, (int)simulationEvent.DurationMs / 2),
            _ => Math.Max(70, (int)simulationEvent.DurationMs / 2)
        };

    private static int Scale(int milliseconds, double speed) =>
        Math.Max(60, (int)Math.Round(milliseconds / Math.Clamp(speed, 0.5d, 4d)));

    private sealed record DynamicChild(string Name, string SourceText);
}
