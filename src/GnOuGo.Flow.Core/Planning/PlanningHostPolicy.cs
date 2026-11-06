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
    IPlanningJournalObserver? JournalObserver => null;
}

/// <summary>Optional inspection of evidence already committed by its journal. Grants no replay or planning authority.</summary>
public interface IPlanningJournalObserver
{
    Task ObserveAsync(string collection, string tenant, string session, string key, string value, long version, CancellationToken ct);
}

public static class PlanningJournalObservation
{
    public static async Task PublishAsync(IPlanningJournalObserver? observer, string collection, string tenant, string session,
        string key, string value, long version)
    {
        if (observer is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await observer.ObserveAsync(collection, tenant, session, key, value, version, timeout.Token); }
        // Inspection cannot change receipt persistence, dispatch, admission or recovery outcomes.
        catch (Exception) { }
    }
}

/// <summary>Inspection evidence only. It cannot authorize a transition or execution.</summary>
public sealed record PlanningInspectionEvent(long Sequence, DateTimeOffset TimestampUtc, string Phase,
    string State, string Reason, string? RequestId, string? BlockId, string? StepId, int? Checkpoint,
    string? Objective)
{
    public long PhaseOccurrence { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlanningDiscoveryInspectionEvidence? Discovery { get; init; }
}

public sealed record PlanningDiscoveryInspectionEvidence(string SourceId, string? FocusId, string? Justification,
    string? SnapshotVersion, string? Cursor, string Outcome, string? FailureId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceLabel { get; init; }
}
