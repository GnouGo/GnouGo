using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Assets.Animation;

namespace GnOuGo.Assets.Animation.Server;

public sealed record SimulationRequest
{
    public string Workflow { get; init; } = "";
    public JsonNode? Inputs { get; init; }
    public int? Seed { get; init; }
    public AnimationSceneKind Scene { get; init; } = AnimationSceneKind.Random;
    public double Speed { get; init; } = 1d;
    public SimulationFailureTarget? FailAt { get; init; }
}

public sealed record PreviewDiagnosticDto(
    string Code,
    string Message,
    string Severity,
    string? WorkflowName,
    string? StepId,
    string? Field);

public sealed record FailureTargetDto(string WorkflowName, string StepId, string StepType, string Label);
public sealed record WorkflowSummaryDto(string Name, int StepCount, bool IsEntrypoint);

public sealed record ValidationResponse(
    bool Valid,
    string? Entrypoint,
    IReadOnlyList<PreviewDiagnosticDto> Diagnostics,
    IReadOnlyList<FailureTargetDto> FailureTargets,
    IReadOnlyList<WorkflowSummaryDto> Workflows);

public sealed record SimulationPreparedData(
    string SimulationId,
    int Seed,
    AnimationSceneKind Scene,
    long DurationMs,
    string Svg,
    int ActorCount,
    int StepEventCount,
    int TaskObjectCount,
    int CanvasWidth,
    int CanvasHeight,
    int LaneCount,
    int NodeCount,
    IReadOnlyList<PreviewDiagnosticDto> Warnings);

public sealed record SimulationStreamEnvelope(
    string Type,
    DateTimeOffset Timestamp,
    SimulationPreparedData? Prepared = null,
    SimulationEvent? Event = null);

public sealed record ApiError(string Code, string Message, IReadOnlyList<PreviewDiagnosticDto>? Diagnostics = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SimulationRequest))]
[JsonSerializable(typeof(ValidationResponse))]
[JsonSerializable(typeof(SimulationStreamEnvelope))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(SimulationPreparedData))]
[JsonSerializable(typeof(SimulationEvent))]
[JsonSerializable(typeof(PreviewDiagnosticDto[]))]
[JsonSerializable(typeof(FailureTargetDto[]))]
[JsonSerializable(typeof(WorkflowSummaryDto[]))]
public partial class AnimationServerJsonContext : JsonSerializerContext;
