using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

public sealed partial class EfPlanningSessionStore
{
    internal const string Expired = "agent-expired-planning-v1";

    // The offline accounting inspector holds the owner lease. This path supplies no command or save authority.
    internal async Task<PlanningSession?> ReadEvidenceAsync(string tenant, string id, CancellationToken ct)
    {
        ValidateKey(tenant, id);
        await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenant, id, ct);
        var record = await records.GetAsync(Expired, tenant, id, Author, ct);
        var fence = record is null ? null : JsonNode.Parse(record.Value);
        if (record is not null && (fence?["pinned"]?.GetValue<bool>() != true ||
            fence["tenant"]?.GetValue<string>() != tenant || fence["session"]?.GetValue<string>() != id)) return null;
        if (await ReadHeadAsync(records, tenant, id, ct) is { } head)
            return (await InspectAsync(records, head.Index(), ct, head.Retention)).Session;
        if (fence?["payloadKey"]?.GetValue<string>() is not { } key || PayloadOwner(key) != id) return null;
        var payload = await records.GetAsync(Collection, tenant, key, Author, ct);
        return payload is null ? null : PlanningSessionHistory.Read(payload.Value, tenant, id, false, payload.UpdatedAt,
            fence["revision"]?.GetValue<long>()).Session;
    }

    private static bool RequiresEvidence(PlanningOwnerHead? head, PlanningSession? state, JsonNode? raw) =>
        (head is not null && state is null) || raw?["pendingCall"] is not null ||
        raw?["usage"]?["uncertainCalls"]?.GetValue<long>() > 0 ||
        state?.Usage is { Calls: > 0, CostEstimateAvailable: false } || head?.Retention.Phase == OwnerRetention.Reconciliation ||
        state?.PendingCall is not null || state?.Usage?.UncertainCalls > 0;

    internal IAsyncEnumerable<string> EnumerateOwnerIdsAsync(string tenant, CancellationToken ct) =>
        records.EnumerateKeysAsync(Heads, tenant, "", Author, ct);

    internal async Task<OwnerRetentionInspection> InspectRetentionAsync(string tenant, string id, DateTimeOffset asOf, CancellationToken ct)
    {
        await using var owner = await PlanningSessionLease.TryAcquireAsync(contexts, tenant, id, ct);
        if (owner is null) return new(id, null, null, "active_owner");
        await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenant, id, ct);
        var head = await ReadHeadAsync(records, tenant, id, ct);
        if (head is null) return new(id, null, null, "unavailable");
        var fence = await records.GetAsync(Expired, tenant, id, Author, ct);
        if (fence is not null || !head.Retention.IsDue(asOf))
            return OwnerRetentionInspection.Inspect(id, head.Revision, head.Retention, asOf,
                fence is not null && JsonNode.Parse(fence.Value)?["pinned"]?.GetValue<bool>() != false, fence is not null);
        var payload = await records.GetAsync(Collection, tenant, head.PayloadKey, Author, ct);
        if (payload is null) return new(id, head.Revision, null, "unreadable_root");
        var state = PlanningSessionHistory.Read(payload.Value, tenant, id, false, payload.UpdatedAt, head.Revision).Session;
        return OwnerRetentionInspection.Inspect(id, head.Revision, head.Retention, asOf,
            RequiresEvidence(head, state, JsonNode.Parse(payload.Value)), false);
    }

    public async Task SweepAsync(string tenant, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        async Task VisitAsync(string id, string? key)
        {
            if (!visited.Add(id)) return;
            try { await CollectAsync(tenant, id, key, ct); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or PlanningConflictException)
            {
                logger?.LogWarning("Skipping an unreadable planning owner during collection ({ErrorType}).", ex.GetType().Name);
            }
        }
        await foreach (var id in records.EnumerateKeysAsync(Heads, tenant, "", Author, ct))
            await VisitAsync(id, null);
        await foreach (var key in records.EnumerateKeysAsync(Collection, tenant, "", Author, ct))
            if (PayloadOwner(key) is { } id) await VisitAsync(id, key);
    }

    private async Task CollectAsync(string tenant, string id, string? historicalKey, CancellationToken ct)
    {
        await using var owner = await PlanningSessionLease.TryAcquireAsync(contexts, tenant, id, ct);
        if (owner is null) return;
        await using var write = await PlanningSessionLease.AcquireWriteAsync(contexts, tenant, id, ct);
        var head = await ReadHeadAsync(records, tenant, id, ct);
        if (head is not null && !head.Retention.IsDue(_policy.Clock.GetUtcNow())) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var fence = await records.GetAsync(Expired, tenant, id, Author, ct);
        if (fence is null)
        {
            var queryCancellation = ct;
            var tenantId = tenant;
            var sessionId = id;
            var pointer = head is null ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s =>
                s.TenantId == tenantId && s.SessionId == sessionId, queryCancellation) : null;
            var key = head?.PayloadKey ?? pointer?.PayloadKey ?? historicalKey;
            if (key is null) return;
            var payload = await records.GetAsync(Collection, tenant, key, Author, ct);
            if (payload is null) return;
            var state = PlanningSessionHistory.Read(payload.Value, tenant, id, false, payload.UpdatedAt).Session;
            var raw = JsonNode.Parse(payload.Value);
            var pinned = RequiresEvidence(head, state, raw);
            var marker = new JsonObject { ["tenant"] = tenant, ["session"] = id, ["revision"] = head?.Revision,
                ["expiredAt"] = _policy.Clock.GetUtcNow(), ["status"] = state?.Status, ["approvalHash"] = state?.ApprovedHash,
                ["payloadKey"] = pinned ? key : null, ["pinned"] = pinned, ["usage"] = JsonSerializer.SerializeToNode(state?.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot) };
            fence = await records.UpsertAsync(Expired, tenant, id, marker.ToJsonString(), Author, ct);
        }
        if (JsonNode.Parse(fence.Value)?["pinned"]?.GetValue<bool>() != false) return;
        if (files is not null) await files.ReleaseAsync(tenant, new(FileOwnerKind.Planning, id), ct);
        await PlanningCapabilitySnapshotJournal.ReleaseAsync(records, PlanningSnapshotOwner.Designer, tenant, id, ct);
        await records.DeleteAsync(PlanningSessionService.Cancellations, tenant, id, Author, ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM PlanningSessions WHERE TenantId={tenant} AND SessionId={id}", ct);
        await OwnerRetentionIndex.EnsureTableAsync(db, ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM OwnerRetention WHERE TenantId={tenant} AND OwnerKind='planning' AND OwnerId={id}", ct);
        await foreach (var key in records.EnumerateKeysAsync(Collection, tenant, id + ":", Author, ct))
            if (PayloadOwner(key) == id) await records.DeleteAsync(Collection, tenant, key, Author, ct);
        await records.DeleteAsync(Heads, tenant, id, Author, ct);
    }
}
