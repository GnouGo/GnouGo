using System.Data.Common;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Immutable encrypted revisions with an optimistic EF Core/SQLite index.</summary>
public sealed class EfPlanningSessionStore(IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records,
    OwnerRetentionPolicy? policy = null, ILogger<EfPlanningSessionStore>? logger = null) : IPlanningSessionStore
{
    internal const string Collection = "agent-planning-sessions-v11";
    internal const string Heads = "agent-planning-heads-v1";
    private readonly OwnerRetentionPolicy _policy = policy ?? OwnerRetentionPolicy.Default;
    internal const string Author = "GnOuGo.Agent.Server.Planning";

    /// <summary>Acquire the same exclusive owner lease for offline inspection/recovery. Null means an owner is still active.</summary>
    public Task<FileStream?> TryAcquireLeaseAsync(string tenantId, string sessionId, CancellationToken ct)
    {
        ValidateKey(tenantId, sessionId);
        return PlanningSessionLease.TryAcquireAsync(contexts, tenantId, sessionId, ct);
    }

    public async Task<PlanningSession?> LoadAsync(string tenantId, string sessionId, CancellationToken ct)
    {
        var result = await InspectOwnerAsync(contexts, records, tenantId, sessionId, ct);
        return result?.RequireSession();
    }

    internal static async Task<PlanningSessionInspection?> InspectOwnerAsync(IDbContextFactory<PlanningDbContext> contexts,
        IKeyVaultRecordStore records, string tenantId, string sessionId, CancellationToken ct)
    {
        ValidateKey(tenantId, sessionId);
        if (await ReadHeadAsync(records, tenantId, sessionId, ct) is { } head)
            return await InspectAsync(records, head.Index(), ct, head.Retention);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tenant = tenantId;
        var session = sessionId;
        var queryCancellation = ct;
        var row = await db.Sessions.SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session, queryCancellation);
        return row is null ? null : await InspectAsync(records, row, ct);
    }

    public async Task<bool> TrySaveAsync(PlanningSession snapshot, long? expectedRevision, CancellationToken ct)
    {
        if (snapshot.SchemaVersion != 11) throw new InvalidOperationException("Unsupported planning session schema.");
        ValidateKey(snapshot.Request.TenantId, snapshot.Request.SessionId);
        if (snapshot.Revision < 0 || expectedRevision is { } prior && snapshot.Revision <= prior)
            throw new ArgumentException("A saved planning revision must advance monotonically.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tenant = snapshot.Request.TenantId;
        var session = snapshot.Request.SessionId;
        await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenant, session, ct);
        var previous = await ReadHeadAsync(records, tenant, session, ct);
        var queryCancellation = ct;
        var row = previous is null ? await db.Sessions.SingleOrDefaultAsync(s =>
            s.TenantId == tenant && s.SessionId == session, queryCancellation) : null;
        if ((previous?.Revision ?? row?.Revision) != expectedRevision) return false;

        if (previous is null && row?.PayloadKey.StartsWith(session + ":managed:", StringComparison.Ordinal) == true)
            throw new PlanningConflictException("The committed planning head is missing.");
        if (expectedRevision is null && await HasLegacyPayloadAsync(tenant, session, ct))
            throw new PlanningConflictException("Historical planning revisions exist without their committed index pointer.");

        // An existing EF-only owner keeps its historical retention and selection rules.
        snapshot.Retention = expectedRevision is null ? _policy.Capture() : previous?.Retention;
        var progress = PlanningRetentionCheckpoint.From(snapshot);
        snapshot.Retention = snapshot.Retention?.ForPlanning(snapshot, _policy.Clock.GetUtcNow(), progress != previous?.Progress);
        var payloadKey = session + (snapshot.Retention is null ? ":" : ":managed:") + snapshot.Revision + ":" + Guid.NewGuid().ToString("N");
        await records.UpsertAsync(Collection, tenant, payloadKey, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSession), Author, ct);
        if (snapshot.Retention is { } retention)
        {
            var head = new PlanningOwnerHead(tenant, session, snapshot.Revision, payloadKey, snapshot.Status, snapshot.UpdatedAtUtc.UtcTicks, retention, progress);
            await records.UpsertAsync(Heads, tenant, session, JsonSerializer.Serialize(head, PlanningHeadJson.Default.PlanningOwnerHead), Author, ct);
            // From this point the commit is durable even if the index is unavailable.
            try { await ProjectHeadAsync(contexts, head, CancellationToken.None); }
            catch (Exception ex) when (ex is DbException or DbUpdateException)
            {
                logger?.LogWarning("Planning index projection failed ({ErrorType}); encrypted head is authoritative.", ex.GetType().Name);
            }
            return true;
        }

        row!.Revision = snapshot.Revision;
        row.Status = snapshot.Status;
        row.UpdatedAtTicks = snapshot.UpdatedAtUtc.UtcTicks;
        row.PayloadKey = payloadKey;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException)
        {
            await records.DeleteAsync(Collection, tenant, payloadKey, Author, CancellationToken.None);
            return false;
        }
    }

    private async Task<bool> HasLegacyPayloadAsync(string tenant, string id, CancellationToken ct)
    {
        var prefix = id + ":";
        bool IsLegacy(string key)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var suffix = key.AsSpan(prefix.Length);
            var separator = suffix.IndexOf(':');
            return separator > 0 && separator < suffix.Length - 1 && !suffix[(separator + 1)..].Contains(':') &&
                long.TryParse(suffix[..separator], out _);
        }
        if (records is IKeyVaultRecordReader reader)
        {
            KeyVaultRecordCursor? cursor = null;
            do
            {
                var page = await reader.QueryMetadataAsync(Collection, tenant, prefix, Author, cursor, 100, ct);
                if (page.Records.Any(item => IsLegacy(item.Key))) return true;
                cursor = page.HasMore ? page.Next : null;
            } while (cursor is not null);
            return false;
        }
        return (await records.ListAsync(Collection, tenant, Author, ct)).Any(item => IsLegacy(item.Key));
    }

    private static async Task<PlanningOwnerHead?> ReadHeadAsync(IKeyVaultRecordStore store, string tenant, string id, CancellationToken ct)
    {
        var record = await store.GetAsync(Heads, tenant, id, Author, ct);
        if (record is null) return null;
        var head = JsonSerializer.Deserialize(record.Value, PlanningHeadJson.Default.PlanningOwnerHead);
        if (head is null || head.Tenant != tenant || head.Session != id || head.Revision < 0 || head.Retention is not { IsValid: true } ||
            string.IsNullOrEmpty(head.PayloadKey) || !head.PayloadKey.StartsWith(id + ":managed:" + head.Revision + ":", StringComparison.Ordinal))
            throw new PlanningConflictException("Invalid committed planning head.");
        return head;
    }

    private static async Task ProjectHeadAsync(IDbContextFactory<PlanningDbContext> factory, PlanningOwnerHead head, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tenant = head.Tenant;
        var session = head.Session;
        var queryCancellation = ct;
        var row = await db.Sessions.SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session, queryCancellation);
        if (row is null) { row = head.Index(); db.Sessions.Add(row); }
        row.PayloadKey = head.PayloadKey;
        row.Revision = head.Revision;
        row.Status = head.Status;
        row.UpdatedAtTicks = head.UpdatedAtTicks;
        await OwnerRetentionIndex.EnsureTableAsync(db, ct);
        var projection = await db.Retention.SingleOrDefaultAsync(r =>
            r.TenantId == tenant && r.OwnerKind == "planning" && r.OwnerId == session, queryCancellation);
        OwnerRetentionIndex.Project(db.Retention, projection, tenant, "planning", session, head.Revision, head.Retention);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Rebuild from committed heads. Historical revisions cannot nominate themselves.</summary>
    public async Task RebuildIndexAsync(string tenant, CancellationToken ct)
    {
        async Task Repair(string id)
        {
            await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenant, id, ct);
            if (await ReadHeadAsync(records, tenant, id, ct) is not { } head) return;
            await ProjectHeadAsync(contexts, head, ct);
        }
        if (records is IKeyVaultRecordReader reader)
        {
            KeyVaultRecordCursor? cursor = null;
            do
            {
                var page = await reader.QueryMetadataAsync(Heads, tenant, "", Author, cursor, 100, ct);
                foreach (var item in page.Records) await Repair(item.Key);
                cursor = page.HasMore ? page.Next : null;
            } while (cursor is not null);
        }
        else
            foreach (var item in await records.ListAsync(Heads, tenant, Author, ct)) await Repair(item.Key);
    }

    public async Task<IReadOnlyList<PlanningSession>> ListAsync(string tenantId, CancellationToken ct)
        => (await InspectAllAsync(contexts, records, tenantId, ct)).Where(s => s.Session is not null).Select(s => s.Session!).ToArray();

    internal static async Task<IReadOnlyList<PlanningSessionInspection>> InspectAllAsync(IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tenant = tenantId;
        var queryCancellation = ct;
        var rows = await db.Sessions.AsNoTracking().Where(s => s.TenantId == tenant).OrderByDescending(s => s.UpdatedAtTicks).ToListAsync(queryCancellation);
        var snapshots = new List<PlanningSessionInspection>();
        foreach (var row in rows)
        {
            var head = await ReadHeadAsync(records, tenant, row.SessionId, ct);
            snapshots.Add(await InspectAsync(records, head?.Index() ?? row, ct, head?.Retention));
        }
        return snapshots;
    }

    internal static async Task<PlanningSessionInspection> InspectAsync(IKeyVaultRecordStore records, PlanningSessionIndex row, CancellationToken ct,
        OwnerRetention? expectedRetention = null)
    {
        if (expectedRetention is null && row.PayloadKey.StartsWith(row.SessionId + ":managed:", StringComparison.Ordinal))
            return new(new(row.SessionId, false, "Unavailable session", new DateTimeOffset(row.UpdatedAtTicks, TimeSpan.Zero), "unavailable", false), null);
        var payload = await records.GetAsync(Collection, row.TenantId, row.PayloadKey, Author, ct);
        if (payload is null)
            return new(new(row.SessionId, false, "Unavailable session", new DateTimeOffset(row.UpdatedAtTicks, TimeSpan.Zero), "unavailable", false), null);
        var result = PlanningSessionHistory.Read(payload.Value, row.TenantId, row.SessionId, false, new DateTimeOffset(row.UpdatedAtTicks, TimeSpan.Zero), row.Revision);
        return result.Session?.Retention == expectedRetention ? result : result with { Session = null, Entry = result.Entry with { Available = false } };
    }

    private static void ValidateKey(string tenant, string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
    }
}

internal sealed record PlanningOwnerHead(string Tenant, string Session, long Revision, string PayloadKey, string Status,
    long UpdatedAtTicks, OwnerRetention Retention, PlanningRetentionCheckpoint Progress)
{
    internal PlanningSessionIndex Index() => new() { TenantId = Tenant, SessionId = Session, Revision = Revision,
        PayloadKey = PayloadKey, Status = Status, UpdatedAtTicks = UpdatedAtTicks };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PlanningOwnerHead))]
internal partial class PlanningHeadJson : JsonSerializerContext;
