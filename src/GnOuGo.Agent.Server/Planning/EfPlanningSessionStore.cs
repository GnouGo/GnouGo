using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Immutable encrypted revisions with an optimistic EF Core/SQLite index.</summary>
public sealed class EfPlanningSessionStore(IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records) : IPlanningSessionStore
{
    internal const string Collection = "agent-planning-snapshots-v2";
    internal const string Author = "GnOuGo.Agent.Server.Planning";

    public async Task<PlanningSnapshot?> LoadAsync(string tenantId, string sessionId, CancellationToken ct)
    {
        ValidateKey(tenantId, sessionId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.SessionId == sessionId, ct);
        return row is null ? null : await ReadAsync(row, ct);
    }

    public async Task<bool> TrySaveAsync(PlanningSnapshot snapshot, long? expectedRevision, CancellationToken ct)
    {
        ValidateKey(snapshot.Request.TenantId, snapshot.Request.SessionId);
        if (snapshot.Revision < 0 || expectedRevision is { } prior && snapshot.Revision <= prior)
            throw new ArgumentException("A saved planning revision must advance monotonically.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tenant = snapshot.Request.TenantId;
        var session = snapshot.Request.SessionId;
        var row = await db.Sessions.SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session, ct);
        if (expectedRevision is null ? row is not null : row?.Revision != expectedRevision) return false;
        var payloadKey = session + ":" + snapshot.Revision + ":" + Guid.NewGuid().ToString("N");
        await records.UpsertAsync(Collection, tenant, payloadKey, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), Author, ct);
        if (row is null)
        {
            row = new PlanningSessionIndex { TenantId = tenant, SessionId = session };
            db.Sessions.Add(row);
        }
        row.Revision = snapshot.Revision;
        row.Status = snapshot.Status;
        row.UpdatedAtTicks = snapshot.UpdatedAtUtc.UtcTicks;
        row.PayloadKey = payloadKey;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException)
        {
            // The immutable losing payload is unreferenced. Never delete another revision.
            await records.DeleteAsync(Collection, tenant, payloadKey, Author, CancellationToken.None);
            await using var check = await contexts.CreateDbContextAsync(ct);
            var current = await check.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session, ct);
            if (current is not null && current.Revision != expectedRevision) return false;
            throw;
        }
    }

    public async Task<IReadOnlyList<PlanningSnapshot>> ListAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Sessions.AsNoTracking().Where(s => s.TenantId == tenantId).OrderByDescending(s => s.UpdatedAtTicks).ToListAsync(ct);
        var snapshots = new List<PlanningSnapshot>();
        foreach (var row in rows) snapshots.Add(await ReadAsync(row, ct));
        return snapshots;
    }

    private async Task<PlanningSnapshot> ReadAsync(PlanningSessionIndex row, CancellationToken ct)
    {
        var payload = await records.GetAsync(Collection, row.TenantId, row.PayloadKey, Author, ct)
            ?? throw new InvalidOperationException("The encrypted planning revision is unavailable.");
        var snapshot = JsonSerializer.Deserialize(payload.Value, PlanningJsonContext.Default.PlanningSnapshot)
            ?? throw new InvalidOperationException("The encrypted planning revision is invalid.");
        if (snapshot.Request.TenantId != row.TenantId || snapshot.Request.SessionId != row.SessionId || snapshot.Revision != row.Revision)
            throw new InvalidOperationException("The encrypted planning revision does not match its tenant-scoped index.");
        return snapshot;
    }

    private static void ValidateKey(string tenant, string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
    }
}
