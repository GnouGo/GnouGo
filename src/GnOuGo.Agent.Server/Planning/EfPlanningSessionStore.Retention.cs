using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

public sealed partial class EfPlanningSessionStore
{
    private const string Expired = "agent-expired-planning-v1";

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
            var pinned = (head is not null && state is null) || raw?["pendingCall"] is not null ||
                raw?["usage"]?["uncertainCalls"]?.GetValue<long>() > 0 ||
                state?.Usage is { Calls: > 0, CostEstimateAvailable: false } || head?.Retention.Phase == OwnerRetention.Reconciliation ||
                state?.PendingCall is not null || state?.Usage?.UncertainCalls > 0;
            var marker = new JsonObject { ["tenant"] = tenant, ["session"] = id, ["revision"] = head?.Revision,
                ["expiredAt"] = _policy.Clock.GetUtcNow(), ["status"] = state?.Status, ["approvalHash"] = state?.ApprovedHash,
                ["pinned"] = pinned, ["usage"] = JsonSerializer.SerializeToNode(state?.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot) };
            fence = await records.UpsertAsync(Expired, tenant, id, marker.ToJsonString(), Author, ct);
        }
        if (JsonNode.Parse(fence.Value)?["pinned"]?.GetValue<bool>() != false) return;
        if (files is not null) await files.ReleaseAsync(tenant, new(FileOwnerKind.Planning, id), ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM PlanningSessions WHERE TenantId={tenant} AND SessionId={id}", ct);
        await OwnerRetentionIndex.EnsureTableAsync(db, ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM OwnerRetention WHERE TenantId={tenant} AND OwnerKind='planning' AND OwnerId={id}", ct);
        await foreach (var key in records.EnumerateKeysAsync(Collection, tenant, id + ":", Author, ct))
            if (PayloadOwner(key) == id) await records.DeleteAsync(Collection, tenant, key, Author, ct);
        await records.DeleteAsync(Heads, tenant, id, Author, ct);
    }
}
