using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.EntityFrameworkCore;
using GnOuGo.KeyVault.Core.Services;
using System.Diagnostics;

namespace GnOuGo.Agent.Server.Planning;

public sealed partial class PlanningSessionService
{
    private async Task RequireDiscoveryRecoverySafeAsync(PlanningSession state, CancellationToken ct)
    {
        if (state.Request.TenantId != Tenant || !PlanningDiscoveryRecoveryActions.Eligible(state) || await IsCancellationRequestedAsync(state, ct))
            throw new PlanningConflictException("Discovery recovery is unavailable for this session.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var calls = await db.Calls.AsNoTracking().Where(c => c.TenantId == Tenant && c.SessionId == state.Request.SessionId).ToArrayAsync(ct);
        if (calls.Length < state.ModelCalls || calls.Any(c => c.Status != "completed"))
            throw new PlanningConflictException("Resolve the existing model journal before recovering metadata; no dispatch was replayed.");
        var expected = calls.Select(c => c.PayloadKey).ToHashSet(StringComparer.Ordinal);
        foreach (var collection in new[] { PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection })
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            if (records is IKeyVaultRecordReader reader)
            {
                KeyVaultRecordCursor? cursor = null;
                do
                {
                    var page = await reader.QueryMetadataAsync(collection, Tenant, state.Request.SessionId + ":", EfPlanningSessionStore.Author, cursor, 100, ct);
                    present.UnionWith(page.Records.Select(r => r.Key)); cursor = page.Next;
                    if (!page.HasMore) break;
                } while (true);
            }
            else foreach (var key in expected)
                if (await records.GetAsync(collection, Tenant, key, EfPlanningSessionStore.Author, ct) is not null) present.Add(key);
            if (!expected.SetEquals(present)) throw new PlanningConflictException("The exact request/receipt journal is incomplete; discovery recovery cannot bypass it.");
        }
        try { foreach (var guard in discoveryRecoveryGuards ?? []) await guard.VerifyAsync(state, ct); }
        catch (InvalidOperationException) { throw new PlanningConflictException("The session owner has forbidden discovery recovery."); }
    }

    internal async Task<bool> CanRecoverDiscoveryAsync(PlanningSession state, CancellationToken ct)
    {
        if (!PlanningDiscoveryRecoveryActions.Eligible(state)) return false;
        await using var lease = await PlanningSessionLease.TryAcquireAsync(contexts, Tenant, state.Request.SessionId, ct);
        if (lease is null) return false;
        try { await RequireDiscoveryRecoverySafeAsync(state, ct); return true; }
        catch (PlanningConflictException) { return false; }
    }

    private async Task<PlanningSession> RecoverDiscoveryAsync(PlanningSession current, PlanningCommand command, CancellationToken ct)
    {
        using var activity = Activities.StartActivity("planning.discovery.recovery", ActivityKind.Internal, default(ActivityContext),
            tags: new ActivityTagsCollection { ["tenant.id"] = Tenant, ["gnougo.planning.session_id"] = current.Request.SessionId,
                ["gnougo.discovery.resolution"] = command.Kind == "resume_discovery" ? "resume" : command.DiscoveryRecovery?.Resolution switch
                { "continue_available" => "continue_available", "refresh_source" => "refresh_source", _ => "invalid" } });
        if (hostPolicy is not null) await hostPolicy.VerifyAsync(current, ct);
        else PlanningTokenThresholds.RejectReserved(current.Request.Options);
        // The metadata-only path deliberately creates no model journal/budget owner.
        await using var runtime = await runtimeFactory.CreateAsync(ct, current.Request);
        var engine = runtime.ConfigureAgentRunners(new WorkflowEngine { McpClientFactory = runtime.McpClientFactory });
        var revision = current.Revision;
        var adapter = new WorkflowPlanningRuntime(engine, async (state, token) =>
        {
            if (state.Revision <= revision) state.Revision = revision + 1;
            if (!await store.TrySaveAsync(state, revision, token)) throw new PlanningConflictException("A newer discovery checkpoint exists.");
            revision = state.Revision;
        }, capabilities: null, capabilitySnapshots: new PlanningCapabilitySnapshotJournal(records), discoveryRecoveryGuard: RequireDiscoveryRecoverySafeAsync);
        return await planner.AdvanceAsync(current, command, adapter, ct);
    }
}
