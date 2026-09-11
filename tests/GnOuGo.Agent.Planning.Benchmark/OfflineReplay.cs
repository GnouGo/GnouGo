using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class OfflineReplay
{
    internal static async Task RunAsync(IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records,
        string tenant, string session, long revision, CancellationToken ct)
    {
        var store = new EfPlanningSessionStore(contexts, records);
        var latest = await store.LoadAsync(tenant, session, ct) ?? throw new InvalidOperationException("Session not found.");
        var before = Fingerprint(latest);
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, tenant, session, EfPlanningSessionStore.Author, ct);
        var captured = (await records.ListAsync(EfPlanningSessionStore.Collection, tenant, EfPlanningSessionStore.Author, ct))
            .Where(r => r.Key.StartsWith(session + ":" + revision + ":", StringComparison.Ordinal)).ToArray();
        if (captured.Length != 1) throw new InvalidOperationException("Replay requires one unambiguous captured revision.");
        var state = PlanningSnapshotPayload.Decode(captured[0].Value);
        if (state.Request.TenantId != tenant || state.Request.SessionId != session || state.Revision != revision)
            throw new InvalidOperationException("The captured revision does not match the requested owner.");
        if (state.PreparationCheckpoint?.ValidatedResults["discovery"] is not JsonArray discovery)
            throw new InvalidOperationException("A captured catalog is required for offline replay.");

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Calls.AsNoTracking().Where(c => c.TenantId == tenant && c.SessionId == session).ToListAsync(ct);
        var evidence = new Dictionary<string, (LLMRequest, LLMResponse?)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var request = await records.GetAsync(PlanningModelJournal.RequestCollection, tenant, row.PayloadKey, EfPlanningSessionStore.Author, ct);
            if (request is null) continue;
            var receipt = await records.GetAsync(PlanningModelJournal.Collection, tenant, row.PayloadKey, EfPlanningSessionStore.Author, ct);
            evidence.Add(row.RequestHash, (JsonSerializer.Deserialize(request.Value, PlanningJsonContext.Default.LLMRequest)!,
                receipt is null ? null : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)));
        }
        var client = new ReceiptOnlyClient(session, evidence);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine
        {
            LLMClient = client, McpClientFactory = new FrozenCatalog(discovery),
            Limits = new() { LogStepContent = false, TenantId = tenant, RunId = session }
        }, (_, _) => Task.CompletedTask);
        var planner = new TypedWorkflowPlanner();
        var advances = 0;
        while (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status))
        {
            if (++advances > 1000) throw new InvalidOperationException("Offline replay made no bounded terminal progress.");
            state = await planner.AdvanceAsync(state, new() { Kind = "advance", ExpectedRevision = state.Revision }, runtime, ct);
        }
        var after = await store.LoadAsync(tenant, session, ct);
        var afterBudget = await records.GetAsync(PlanningBudgetSink.Collection, tenant, session, EfPlanningSessionStore.Author, ct);
        var afterRows = await db.Calls.AsNoTracking().Where(c => c.TenantId == tenant && c.SessionId == session).ToListAsync(ct);
        if (after is null || Fingerprint(after) != before || afterBudget?.Value != budget?.Value ||
            !rows.Select(r => (r.RequestHash, r.Status, r.PayloadKey)).Order().SequenceEqual(afterRows.Select(r => (r.RequestHash, r.Status, r.PayloadKey)).Order()))
            throw new InvalidOperationException("The source changed during offline replay; the result is not comparable.");
        Console.WriteLine(new JsonObject
        {
            ["session"] = session, ["sourceRevision"] = revision, ["sourceUnchanged"] = true,
            ["status"] = state.Status, ["phase"] = state.CurrentPhase, ["localAdvances"] = advances,
            ["replayedReceipts"] = client.Replayed.Count, ["providerDispatches"] = 0,
            ["behaviorPresent"] = state.BehaviorPlan is not null, ["graphPresent"] = state.Graph is not null,
            ["diagnostics"] = new JsonArray(state.Diagnostics.Select(d => (JsonNode)new JsonObject { ["code"] = d.Code, ["location"] = d.Location }).ToArray())
        }.ToJsonString());
    }

    private static string Fingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot));
}
