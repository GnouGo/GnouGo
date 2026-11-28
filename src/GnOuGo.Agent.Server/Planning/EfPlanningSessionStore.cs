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
public sealed partial class EfPlanningSessionStore(IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records,
    OwnerRetentionPolicy? policy = null, ILogger<EfPlanningSessionStore>? logger = null, GnOuGo.Files.Core.DurableFileStore? files = null) : IPlanningSessionStore
{
    internal const string Collection = "agent-planning-sessions-v11";
    internal const string Heads = "agent-planning-heads-v1";
    private readonly OwnerRetentionPolicy _policy = policy ?? OwnerRetentionPolicy.Default;
    internal const string Author = "GnOuGo.Agent.Server.Planning";

    /// <summary>Acquire a live planning owner for a command. An expired owner cannot resume.</summary>
    public async Task<FileStream?> TryAcquireLeaseAsync(string tenantId, string sessionId, CancellationToken ct)
    {
        ValidateKey(tenantId, sessionId);
        var lease = await PlanningSessionLease.TryAcquireAsync(contexts, tenantId, sessionId, ct);
        if (lease is null) return null;
        try
        {
            await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenantId, sessionId, ct);
            if (await records.GetAsync(Expired, tenantId, sessionId, Author, ct) is not null ||
                (await ReadHeadAsync(records, tenantId, sessionId, ct))?.Retention.IsDue(_policy.Clock.GetUtcNow()) == true)
                throw new PlanningConflictException("owner_expired");
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async Task<PlanningSession?> LoadAsync(string tenantId, string sessionId, CancellationToken ct)
    {
        var result = await InspectOwnerAsync(contexts, records, tenantId, sessionId, ct, _policy.Clock);
        return result?.RequireSession();
    }

    internal static async Task<PlanningSessionInspection?> InspectOwnerAsync(IDbContextFactory<PlanningDbContext> contexts,
        IKeyVaultRecordStore records, string tenantId, string sessionId, CancellationToken ct, TimeProvider? clock = null)
    {
        ValidateKey(tenantId, sessionId);
        await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenantId, sessionId, ct);
        if (await records.GetAsync(Expired, tenantId, sessionId, Author, ct) is not null) return null;
        var head = await ReadHeadAsync(records, tenantId, sessionId, ct);
        if (head is null || await DueAsync(contexts, head, clock ?? TimeProvider.System, ct)) return null;
        return await InspectAsync(records, head.Index(), ct, head.Retention);
    }

    private static async Task<bool> DueAsync(IDbContextFactory<PlanningDbContext> contexts, PlanningOwnerHead head, TimeProvider clock, CancellationToken ct)
    {
        if (head.Retention.PolicyVersion != OwnerRetentionPolicy.CurrentVersion) return true;
        if (!head.Retention.IsDue(clock.GetUtcNow())) return false;
        if (head.Retention.Phase != OwnerRetention.Active) return true;
        await using var free = await PlanningSessionLease.TryAcquireAsync(contexts, head.Tenant, head.Session, ct);
        return free is not null;
    }

    public async Task<bool> TrySaveAsync(PlanningSession snapshot, long? expectedRevision, CancellationToken ct)
    {
        if (snapshot.SchemaVersion != 11) throw new InvalidOperationException("Unsupported planning session schema.");
        ValidateKey(snapshot.Request.TenantId, snapshot.Request.SessionId);
        if (snapshot.Revision < 0 || expectedRevision is { } prior && snapshot.Revision <= prior)
            throw new ArgumentException("A saved planning revision must advance monotonically.");
        var tenant = snapshot.Request.TenantId;
        var session = snapshot.Request.SessionId;
        await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenant, session, ct);
        var previous = await ReadHeadAsync(records, tenant, session, ct);
        if (await records.GetAsync(Expired, tenant, session, Author, ct) is not null ||
            previous is not null && await DueAsync(contexts, previous, _policy.Clock, ct))
            throw new PlanningConflictException("owner_expired");
        if (previous?.Revision != expectedRevision) return false;
        if (previous is null)
        {
            // A lost or retired head cannot make an old ID available for another dispatch.
            await foreach (var key in records.EnumerateKeysAsync(Collection, tenant, session + ":", Author, ct))
                if (PayloadOwner(key) == session) throw new PlanningConflictException("Planning ID already used.");
        }
        var progress = PlanningRetentionCheckpoint.From(snapshot);
        snapshot.Retention = (previous?.Retention ?? _policy.Capture()).ForPlanning(snapshot, _policy.Clock.GetUtcNow(), progress != previous?.Progress);
        var payloadKey = session + ":managed:" + snapshot.Revision + ":" + Guid.NewGuid().ToString("N");
        await records.UpsertAsync(Collection, tenant, payloadKey, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSession), Author, ct);
        var head = new PlanningOwnerHead(tenant, session, snapshot.Revision, payloadKey, snapshot.Status,
            snapshot.UpdatedAtUtc.UtcTicks, snapshot.Retention, progress);
        await records.UpsertAsync(Heads, tenant, session, JsonSerializer.Serialize(head, PlanningHeadJson.Default.PlanningOwnerHead), Author, ct);
        try { await ProjectHeadAsync(contexts, head, CancellationToken.None); }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            logger?.LogWarning("Planning index projection failed ({ErrorType}).", ex.GetType().Name);
        }
        return true;
    }

    private static string? PayloadOwner(string key)
    {
        var suffix = key.LastIndexOf(':');
        if (suffix <= 0) return null;
        var revision = key.LastIndexOf(':', suffix - 1);
        if (revision <= 0 || !long.TryParse(key.AsSpan(revision + 1, suffix - revision - 1), out _)) return null;
        var owner = key[..revision];
        return owner.EndsWith(":managed", StringComparison.Ordinal) ? owner[..^8] : owner;
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
            if (await records.GetAsync(Expired, tenant, id, Author, ct) is not null ||
                await ReadHeadAsync(records, tenant, id, ct) is not { } head || await DueAsync(contexts, head, _policy.Clock, ct)) return;
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
        => (await InspectAllAsync(contexts, records, tenantId, ct, _policy.Clock)).Where(s => s.Session is not null).Select(s => s.Session!).ToArray();

    internal static async Task<IReadOnlyList<PlanningSessionInspection>> InspectAllAsync(IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records, string tenantId, CancellationToken ct, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tenant = tenantId;
        var queryCancellation = ct;
        var rows = await db.Sessions.AsNoTracking().Where(s => s.TenantId == tenant).OrderByDescending(s => s.UpdatedAtTicks).ToListAsync(queryCancellation);
        var snapshots = new List<PlanningSessionInspection>();
        foreach (var row in rows)
        {
            if (await InspectOwnerAsync(contexts, records, tenant, row.SessionId, ct, clock) is { } entry) snapshots.Add(entry);
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
