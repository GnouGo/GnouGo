using System.Text.Json;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Complete immutable metadata, never execution or binding authority.</summary>
public sealed record CapabilitySourceSnapshot(string SourceId, string ConfigurationFingerprint, List<PlanningCapability> Capabilities);
public sealed record CapabilitySnapshotReference(string TenantId, string SessionId, string ActionId, string SourceId,
    string ConfigurationFingerprint, string Version, string Key);

/// <summary>Optional catalog capability. Older catalogs keep their original continuation protocol.</summary>
public interface ICapabilitySnapshotCatalog
{
    Task<CapabilitySourceSnapshot> CaptureAsync(string sourceId, CancellationToken ct);
    CapabilityPage ReadPage(CapabilitySourceSnapshot snapshot, string? cursor, string? query, string? producedArtifactKind = null);
}

/// <summary>Implemented by existing planning journal owners. Publish before checkpointing the reference.</summary>
public interface IPlanningCapabilitySnapshotJournal
{
    Task<CapabilitySnapshotReference> PublishAsync(string tenant, string session, string action, CapabilitySourceSnapshot snapshot, CancellationToken ct);
    Task<CapabilitySourceSnapshot> ReadAsync(CapabilitySnapshotReference reference, string tenant, string session, string action, string source, CancellationToken ct);
}

public sealed record CapabilityDiscoveryFailure(string Id, string SourceId, string? FocusId, string Query,
    string? Cursor, string Category, string Message, string? SnapshotVersion, string? RequestId,
    string? Justification, DateTimeOffset TimestampUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; init; }
    // Present only for an exact selected contract, never inferred for history.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CapabilityId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractVersion { get; init; }
}

public sealed record PlanningDiscoveryRecovery(string FailureId, string Resolution);

/// <summary>Optional owner guard, e.g. an existing campaign's permanent-retention barrier.</summary>
public interface IPlanningDiscoveryRecoveryGuard
{
    Task VerifyAsync(PlanningSession session, CancellationToken ct);
}

/// <summary>Safe categories, independent of provider exception messages or endpoint secrets.</summary>
public sealed class CapabilityDiscoveryException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CapabilitySourceSnapshot))]
public partial class CapabilitySnapshotJsonContext : JsonSerializerContext;
