namespace GnOuGo.Flow.Core.Planning;

/// <summary>Injected by a trusted host, never resolved from workflow or model input.
/// Ordinary runtime factories reject reserved options when this service is absent.</summary>
public interface IPlanningHostPolicy
{
    Task CaptureAsync(PlanningSession session, CancellationToken ct);
    Task CaptureAsync(PlanningSession session, PlanningSession creation, CancellationToken ct) => CaptureAsync(session, ct);
    Task VerifyAsync(PlanningSession session, CancellationToken ct);
    Task VerifyCreationAsync(PlanningSession session, CancellationToken ct) => VerifyAsync(session, ct);
    Task<IPlanningRuntimeModel?> CreateModelAsync(PlanningSession session, CancellationToken ct) => Task.FromResult<IPlanningRuntimeModel?>(null);
}

public interface IPlanningRuntimeModel : IAsyncDisposable
{
    GnOuGo.Flow.Core.Runtime.ILLMClient Client { get; }
    GnOuGo.Flow.Core.Runtime.ILLMCapabilityResolver? Capabilities { get; }
    GnOuGo.Flow.Core.Runtime.IModelUsageCostEstimator CostEstimator { get; }
}

/// <summary>Inspection evidence only. It cannot authorize a transition or execution.</summary>
public sealed record PlanningInspectionEvent(long Sequence, DateTimeOffset TimestampUtc, string Phase,
    string State, string Reason, string? RequestId, string? BlockId, string? StepId, int? Checkpoint,
    string? Objective)
{
    public long PhaseOccurrence { get; init; }
}
