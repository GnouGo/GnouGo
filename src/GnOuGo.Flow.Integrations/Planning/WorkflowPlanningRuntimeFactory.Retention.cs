using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Planning;

public sealed partial class WorkflowPlanningRuntimeFactory
{
    private const string Expired = "flow-expired-planning-v1";

    private FileStream? TryOwner(string tenant, string id)
    {
        Directory.CreateDirectory(leaseDirectory);
        var path = Path.Combine(leaseDirectory, PlanningGraphCompiler.Fingerprint(tenant + "\n" + id) + ".lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { return null; }
    }

    public async Task<bool> AvailableAsync(string tenant, string id, OwnerRetention? policy, CancellationToken ct)
    {
        if (policy is not { IsValid: true, PolicyVersion: OwnerRetentionPolicy.CurrentVersion } || await records.GetAsync(Expired, tenant, id, Author, ct) is not null) return false;
        if (!policy.IsDue(retention.Clock.GetUtcNow())) return true;
        if (policy.Phase != OwnerRetention.Active) return false;
        using var free = TryOwner(tenant, id);
        return free is null;
    }

    public async Task SweepAsync(string tenant, CancellationToken ct)
    {
        await foreach (var id in records.EnumerateKeysAsync(Sessions, tenant, "", Author, ct))
        {
            using var lease = TryOwner(tenant, id);
            if (lease is null) continue;
            var saved = await records.GetAsync(Sessions, tenant, id, Author, ct);
            if (saved is null) continue;
            var fence = await records.GetAsync(Expired, tenant, id, Author, ct);
            if (fence is null)
            {
                var raw = JsonNode.Parse(saved.Value);
                var policy = raw?["retention"]?.Deserialize(PlanningJsonContext.Default.OwnerRetention);
                if (policy is { IsValid: true } && !policy.IsDue(retention.Clock.GetUtcNow())) continue;
                var pinned = policy?.Phase == OwnerRetention.Reconciliation || raw?["pendingCall"] is not null ||
                    raw?["usage"]?["uncertainCalls"]?.GetValue<long>() > 0 ||
                    raw?["usage"]?["calls"]?.GetValue<long>() > 0 && raw["usage"]?["costEstimateAvailable"]?.GetValue<bool>() != true;
                var marker = new JsonObject { ["tenant"] = tenant, ["session"] = id, ["expiredAt"] = retention.Clock.GetUtcNow(),
                    ["pinned"] = pinned, ["approvedHash"] = raw?["approvedHash"]?.DeepClone(), ["usage"] = raw?["usage"]?.DeepClone() };
                fence = await records.UpsertAsync(Expired, tenant, id, marker.ToJsonString(), Author, ct);
            }
            if (JsonNode.Parse(fence.Value)?["pinned"]?.GetValue<bool>() != false) continue;
            if (files is not null) await files.ReleaseAsync(tenant, new(FileOwnerKind.WorkflowPlanning, id), ct);
            await records.DeleteAsync(Definitions, tenant, id, Author, ct);
            await records.DeleteAsync(Sessions, tenant, id, Author, ct);
        }
        // Creation publishes the definition before the first checkpoint. A process can die in between.
        await foreach (var id in records.EnumerateKeysAsync(Definitions, tenant, "", Author, ct))
        {
            using var owner = TryOwner(tenant, id);
            if (owner is null || await records.GetAsync(Sessions, tenant, id, Author, ct) is not null) continue;
            var record = await records.GetAsync(Definitions, tenant, id, Author, ct);
            if (record is null) continue;
            var checkpoint = JsonNode.Parse(record.Value)?["creationCheckpoint"]?["session"];
            var captured = checkpoint?["retention"]?.Deserialize(PlanningJsonContext.Default.OwnerRetention);
            if (captured is { IsValid: true } && !captured.IsDue(retention.Clock.GetUtcNow())) continue;
            var fence = await records.GetAsync(Expired, tenant, id, Author, ct);
            if (fence is null)
            {
                var pin = await records.GetAsync(WorkflowPlanningBudgetSink.Collection, tenant, id, Author, ct) is not null;
                var marker = new JsonObject { ["tenant"] = tenant, ["session"] = id, ["expiredAt"] = retention.Clock.GetUtcNow(), ["pinned"] = pin };
                fence = await records.UpsertAsync(Expired, tenant, id, marker.ToJsonString(), Author, ct);
            }
            if (JsonNode.Parse(fence.Value)?["pinned"]?.GetValue<bool>() != false) continue;
            if (files is not null) await files.ReleaseAsync(tenant, new(FileOwnerKind.WorkflowPlanning, id), ct);
            await records.DeleteAsync(Definitions, tenant, id, Author, ct);
        }
    }
}
