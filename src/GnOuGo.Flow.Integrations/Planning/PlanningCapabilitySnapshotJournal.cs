using GnOuGo.Flow.Planning;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Planning;

/// <summary>Immutable metadata payloads in the existing encrypted planning journal boundary.</summary>
public sealed class PlanningCapabilitySnapshotJournal(IKeyVaultRecordStore records) : IPlanningCapabilitySnapshotJournal
{
    public const string Collection = "planning-capability-snapshots-v11";
    private const string Author = "GnOuGo.Flow.Planning.CapabilitySnapshots";
    // Scoped to this runtime transition. Reuse immutable metadata, never planning progress.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (CapabilitySnapshotReference Reference, CapabilitySourceSnapshot Snapshot)> _read = new(StringComparer.Ordinal);

    public async Task<CapabilitySnapshotReference> PublishAsync(string tenant, string session, string action, CapabilitySourceSnapshot snapshot, CancellationToken ct)
    {
        ValidateScope(tenant, session, action, snapshot.SourceId);
        var text = JsonSerializer.Serialize(snapshot, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot);
        var version = PlanningGraphCompiler.Fingerprint(text);
        var key = Key(tenant, session, action, snapshot.SourceId, version);
        var existing = await records.GetAsync(Collection, tenant, key, Author, ct);
        if (existing is not null && existing.Value != text) throw new PlanningConflictException("The immutable catalog snapshot changed.");
        if (existing is null) await records.UpsertAsync(Collection, tenant, key, text, Author, ct);
        var reference = new CapabilitySnapshotReference(tenant, session, action, snapshot.SourceId, snapshot.ConfigurationFingerprint, version, key);
        _read[key] = (reference, snapshot);
        return reference;
    }

    public async Task<CapabilitySourceSnapshot> ReadAsync(CapabilitySnapshotReference reference, string tenant, string session, string action, string source, CancellationToken ct)
    {
        ValidateScope(tenant, session, action, source);
        if (reference.TenantId != tenant || reference.SessionId != session || reference.ActionId != action || reference.SourceId != source ||
            reference.Key != Key(tenant, session, action, source, reference.Version))
            throw new PlanningConflictException("The catalog snapshot belongs to another discovery action.");
        if (records is IKeyVaultRecordReader reader)
        {
            var metadata = await reader.GetMetadataAsync(Collection, tenant, reference.Key, Author, ct)
                ?? throw new PlanningConflictException("The retained catalog snapshot is unavailable; no source was refetched.");
            if (metadata.Version is { } version && !version.Equals(reference.Version, StringComparison.OrdinalIgnoreCase))
                throw new PlanningConflictException("The retained catalog snapshot is corrupt.");
            if (metadata.Version is not null && _read.TryGetValue(reference.Key, out var cached) && cached.Reference == reference)
            {
                if (PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(cached.Snapshot, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot)) != reference.Version)
                    throw new PlanningConflictException("The retained in-memory metadata snapshot was modified.");
                return cached.Snapshot;
            }
        }
        var row = await records.GetAsync(Collection, tenant, reference.Key, Author, ct)
            ?? throw new PlanningConflictException("The retained catalog snapshot is unavailable; no source was refetched.");
        if (PlanningGraphCompiler.Fingerprint(row.Value) != reference.Version) throw new PlanningConflictException("The retained catalog snapshot is corrupt.");
        CapabilitySourceSnapshot snapshot;
        try { snapshot = JsonSerializer.Deserialize(row.Value, CapabilitySnapshotJsonContext.Default.CapabilitySourceSnapshot)!; }
        catch (JsonException) { throw new PlanningConflictException("The retained catalog snapshot is invalid."); }
        if (snapshot is null || snapshot.SourceId != source || snapshot.ConfigurationFingerprint != reference.ConfigurationFingerprint)
            throw new PlanningConflictException("The catalog snapshot identity is invalid.");
        _read[reference.Key] = (reference, snapshot);
        return snapshot;
    }

    private static string Key(string tenant, string session, string action, string source, string version) =>
        PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(new[] { tenant, session, action, source, version }, PlanningJsonContext.Default.StringArray));

    private static void ValidateScope(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace) || values.Any(v => v.Length > 512))
            throw new PlanningConflictException("Exact tenant, session, action and source identities are required.");
    }
}
