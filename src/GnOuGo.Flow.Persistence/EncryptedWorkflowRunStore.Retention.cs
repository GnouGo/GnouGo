using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Flow.Persistence;

public sealed partial class EncryptedWorkflowRunStore
{
    private const string Expired = "flow-expired-executions-v1";
    private const string LegacyImages = "flow-run-images-v1";

    private async Task<bool> IsFencedAsync(string tenant, string id, CancellationToken ct) =>
        await _records.GetAsync(Expired, tenant, id, Author, ct) is not null;

    private FileStream? TryOwner(string tenant, string id)
    {
        try { return OpenLock(tenant, id, "owner"); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { return null; }
    }

    private bool IsExpired(string tenant, string id, OwnerRetention? retention, bool acquiring)
    {
        if (retention is null || retention.PolicyVersion != OwnerRetentionPolicy.CurrentVersion) return true;
        if (!retention.IsValid) throw new InvalidDataException("Invalid run retention.");
        if (!retention.IsDue(_retention.Clock.GetUtcNow())) return false;
        if (retention.Phase != OwnerRetention.Active || acquiring) return true;
        using var available = TryOwner(tenant, id);
        return available is not null;
    }

    public async Task SweepAsync(string tenant, CancellationToken ct)
    {
        await foreach (var id in _records.EnumerateKeysAsync(CreationIntents, tenant, "", Author, ct))
        {
            await using var write = await WriteLockAsync(tenant, id, ct);
            if (await _records.GetAsync(Collection, tenant, id, Author, ct) is null && !await IsFencedAsync(tenant, id, ct))
                await ReleaseDependenciesAsync(tenant, id, ct);
            await _records.DeleteAsync(CreationIntents, tenant, id, Author, ct);
        }
        await foreach (var id in _records.EnumerateKeysAsync(Collection, tenant, "", Author, ct))
        {
            try { await CollectRunAsync(tenant, id, ct); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or WorkflowRunConflictException)
            {
                _logger.LogWarning("Skipping an unreadable execution owner during collection ({ErrorType}).", ex.GetType().Name);
            }
        }
        await foreach (var key in _records.EnumerateKeysAsync(LegacyImages, tenant, "", Author, ct))
        {
            var separator = key.LastIndexOf('/');
            if (separator <= 0) continue;
            var id = key[..separator];
            using var owner = TryOwner(tenant, id);
            if (owner is null) continue;
            await using var write = await WriteLockAsync(tenant, id, ct);
            if (await _records.GetAsync(Collection, tenant, id, Author, ct) is not null) continue;
            if (await _records.GetAsync(Expired, tenant, id, Author, ct) is { } marker &&
                JsonNode.Parse(marker.Value)?["pinned"]?.GetValue<bool>() != false) continue;
            await _records.DeleteAsync(LegacyImages, tenant, key, Author, ct);
        }
        await Files.SweepAsync(ct);
    }
    private async Task ReleaseDependenciesAsync(string tenant, string id, CancellationToken ct)
    {
        await Files.ReleaseAsync(tenant, new(FileOwnerKind.Execution, id), ct);
        await foreach (var block in _records.EnumerateKeysAsync(SplitJournal.Blocks, tenant, SplitJournal.Prefix(tenant, id), Author, ct))
            await _records.DeleteAsync(SplitJournal.Blocks, tenant, block, Author, ct);
    }

    private async Task CollectRunAsync(string tenant, string id, CancellationToken ct)
    {
        using var owner = TryOwner(tenant, id);
        if (owner is null) return;
        await using var write = await WriteLockAsync(tenant, id, ct);
        var record = await _records.GetAsync(Collection, tenant, id, Author, ct);
        if (record is null) return;
        var fence = await _records.GetAsync(Expired, tenant, id, Author, ct);
        if (fence is null)
        {
            var head = SplitJournal.Parse(record.Value, tenant, id);
            var journal = head is null ? null : new SplitJournal(_records, tenant, id, head);
            if (journal?.CommittedRetention is { } policy && !policy.IsDue(_retention.Clock.GetUtcNow()))
            {
                if (policy.Phase != OwnerRetention.Reconciliation)
                    await journal.CollectOrphansAsync(_retention.Clock.GetUtcNow().AddDays(-1), ct);
                return;
            }
            WorkflowRun? run = null;
            if (journal is not null) run = await journal.ReadAsync(ct);
            else if (JsonNode.Parse(record.Value)?["schemaVersion"]?.GetValue<int>() == 9)
                run = WorkflowRunStorage.Read(record.Value, tenant, id);
            var effects = new JsonArray();
            if (run is not null)
                foreach (var invocation in run.Invocations.Values.Where(i => i.Recovery == StepRecovery.External && i.DispatchedAt.HasValue))
                    effects.Add((JsonNode)new JsonObject { ["invocation"] = invocation.Id, ["stepType"] = invocation.StepType,
                        ["dispatchedAt"] = invocation.DispatchedAt, ["completedAt"] = invocation.CompletedAt,
                        ["status"] = invocation.Status, ["input"] = invocation.ResolvedInput?.DeepClone(),
                        ["receipt"] = invocation.Output?.DeepClone(), ["observation"] = invocation.Observation?.DeepClone() });
            var pinned = run?.Status == WorkflowRunStatus.NeedsReconciliation || run?.ModelUsage?.UncertainCalls > 0 ||
                run?.ModelUsage is { Calls: > 0, CostEstimateAvailable: false } ||
                run?.Retention?.Phase == OwnerRetention.Reconciliation ||
                run?.Invocations.Values.Any(i => i.Recovery == StepRecovery.External && i.DispatchedAt.HasValue && (!i.CompletedAt.HasValue || !i.ExternalCompletionObserved)) == true;
            if (run is null && JsonNode.Parse(record.Value) is JsonObject retired)
                pinned = retired["modelUsage"] is not null || retired["invocations"] is JsonObject { Count: > 0 };
            var marker = new JsonObject { ["owner"] = id, ["tenant"] = tenant, ["revision"] = run?.Revision,
                ["definitionHash"] = run?.DefinitionHash, ["status"] = run?.Status, ["expiredAt"] = _retention.Clock.GetUtcNow(),
                ["pinned"] = pinned, ["effects"] = effects,
                ["usage"] = JsonSerializer.SerializeToNode(run?.ModelUsage, WorkflowRunJsonContext.Default.LLMUsageBudgetSnapshot) };
            fence = await _records.UpsertAsync(Expired, tenant, id, marker.ToJsonString(), Author, ct);
        }
        if (JsonNode.Parse(fence.Value)?["pinned"]?.GetValue<bool>() != false) return;
        // The tombstone blocks reads while any subset of these steps is repeated after a crash.
        await ReleaseDependenciesAsync(tenant, id, ct);
        await using var db = new WorkflowRunDbContext(_options);
        if (await db.Database.CanConnectAsync(ct))
        {
            await EnsureRunTableAsync(db, ct);
            await db.Database.ExecuteSqlAsync($"DELETE FROM WorkflowRuns WHERE TenantId={tenant} AND RunId={id}", ct);
            await OwnerRetentionIndex.EnsureTableAsync(db, ct);
            await db.Database.ExecuteSqlAsync($"DELETE FROM OwnerRetention WHERE TenantId={tenant} AND OwnerKind='execution' AND OwnerId={id}", ct);
        }
        await _records.DeleteAsync(Collection, tenant, id, Author, ct);
    }

}
