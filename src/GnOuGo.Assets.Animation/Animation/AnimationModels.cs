using System.Text.Json.Nodes;
using GnOuGo.Assets.Animation.Preview;

namespace GnOuGo.Assets.Animation;

public enum AnimationSceneKind
{
    Random,
    Office,
    Meadow,
    Kitchen
}

public enum AnimationActorKind
{
    Master,
    Worker,
    Clone
}

public enum AnimationStationKind
{
    Ai,
    Mcp,
    Writing,
    Planning,
    Human,
    Handoff,
    Workbench,
    KeyboardDesk,
    HandoffDesk,
    DeliveryDock
}

public enum AnimationFlowNodeKind
{
    Start,
    Desk,
    WorkflowCall,
    Fork,
    Join,
    Decision,
    Loop,
    Return,
    Finish,
    Delivery
}

public enum AnimationFlowEdgeKind
{
    Sequence,
    Branch,
    Loop,
    Handoff,
    Return,
    Waiting
}

public enum SimulationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

public readonly record struct AnimationPoint(double X, double Y);

public sealed record AnimationActor(
    string Id,
    string WorkflowName,
    string Label,
    AnimationActorKind Kind,
    int VisualSeed,
    AnimationPoint Home,
    string? CloneOfActorId = null,
    bool InitiallyVisible = false);

public sealed record AnimationStation(
    string Id,
    string Label,
    AnimationStationKind Kind,
    AnimationPoint Position,
    string? WorkflowInstanceId = null,
    string? WorkflowName = null,
    string? StepId = null,
    string? StepType = null);

public sealed record AnimationWorkflowLane(
    string Id,
    string WorkflowInstanceId,
    string WorkflowName,
    string ActorId,
    string Label,
    double X,
    double Width,
    double StartY,
    double EndY,
    bool IsEntrypoint);

public sealed record AnimationFlowNode(
    string Id,
    string LaneId,
    string WorkflowInstanceId,
    string WorkflowName,
    string Label,
    AnimationFlowNodeKind Kind,
    AnimationPoint Position,
    string? StepId = null,
    string? StepType = null,
    string? StationId = null,
    string? BranchId = null,
    bool IsSelected = true);

public sealed record AnimationFlowEdge(
    string Id,
    string FromNodeId,
    string ToNodeId,
    AnimationFlowEdgeKind Kind,
    string? Label = null,
    bool IsSelected = true);

public sealed record AnimationSceneBounds(double Width, double Height);

public sealed record AnimationTaskObject(
    string Id,
    string Label,
    string Kind,
    string InitialActorId,
    bool InitiallyVisible = false,
    string? WorkflowName = null,
    string? StepId = null);

public sealed record SimulationFailureTarget(string WorkflowName, string StepId);

public sealed record GnouGnouAnimationOptions
{
    public int Seed { get; init; } = 1;
    public AnimationSceneKind Scene { get; init; } = AnimationSceneKind.Random;
    public JsonNode? Inputs { get; init; }
    public SimulationFailureTarget? FailAt { get; init; }
    public int MoveDurationMs { get; init; } = 600;
    public int WorkDurationMs { get; init; } = 900;
    public int HandoffDurationMs { get; init; } = 500;
    public int EffectDurationMs { get; init; } = 700;
    public int MaxStepOccurrences { get; init; } = 200;
    public int MaxWorkflowActors { get; init; } = 32;
    public int MaxClonesPerFork { get; init; } = 16;
    public int MaxPreviewLoopIterations { get; init; } = 5;
}

public static class SimulationEventTypes
{
    public const string SimulationStarted = "simulation.started";
    public const string SimulationCompleted = "simulation.completed";
    public const string WorkflowStarted = "workflow.started";
    public const string WorkflowCompleted = "workflow.completed";
    public const string ActorSpawned = "actor.spawned";
    public const string ActorMoved = "actor.moved";
    public const string ActorWaiting = "actor.waiting";
    public const string ActorCloned = "actor.cloned";
    public const string ActorMerged = "actor.merged";
    public const string TaskDropped = "task.dropped";
    public const string TaskPickedUp = "task.picked_up";
    public const string TaskHandedOff = "task.handed_off";
    public const string TaskCloned = "task.cloned";
    public const string TaskMerged = "task.merged";
    public const string TaskCompleted = "task.completed";
    public const string OutputSent = "output.sent";
    public const string StepStarted = "step.started";
    public const string StepCompleted = "step.completed";
    public const string StepSkipped = "step.skipped";
    public const string ParallelStarted = "parallel.started";
    public const string ParallelCompleted = "parallel.completed";
    public const string DecisionSimulated = "decision.simulated";
}

public sealed record SimulationEvent
{
    public int Sequence { get; init; }
    public string Type { get; init; } = "";
    public long OffsetMs { get; init; }
    public long DurationMs { get; init; }
    public string? WorkflowInstanceId { get; init; }
    public string? WorkflowName { get; init; }
    public string? ActorId { get; init; }
    public string? TargetActorId { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public string? StationId { get; init; }
    public string? NodeId { get; init; }
    public string? TargetNodeId { get; init; }
    public string? EdgeId { get; init; }
    public string? TaskId { get; init; }
    public string? BranchId { get; init; }
    public SimulationStatus? Status { get; init; }
    public int? ProgressCurrent { get; init; }
    public int? ProgressTotal { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public string? Message { get; init; }
}

public sealed class GnouGnouAnimationPlan
{
    public required int Seed { get; init; }
    public required AnimationSceneKind Scene { get; init; }
    public required string Entrypoint { get; init; }
    public required IReadOnlyList<AnimationActor> Actors { get; init; }
    public required IReadOnlyList<AnimationStation> Stations { get; init; }
    public required IReadOnlyList<AnimationWorkflowLane> Lanes { get; init; }
    public required IReadOnlyList<AnimationFlowNode> Nodes { get; init; }
    public required IReadOnlyList<AnimationFlowEdge> Edges { get; init; }
    public required AnimationSceneBounds Bounds { get; init; }
    public required IReadOnlyList<AnimationTaskObject> Tasks { get; init; }
    public required IReadOnlyList<SimulationEvent> Events { get; init; }
    public required IReadOnlyList<WorkflowPreviewDiagnostic> Warnings { get; init; }
    public required long DurationMs { get; init; }
}

public sealed class GnouGnouAnimationPlanException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record GnouGnouAnimationRenderOptions
{
    public int Width { get; init; } = 1600;
    public int Height { get; init; } = 900;
    public string? Title { get; init; }
    public string? Description { get; init; }
}

public sealed record GnouGnouAnimationSvgDocument(
    string Svg,
    int Width,
    int Height,
    AnimationSceneKind Scene,
    long DurationMs);
