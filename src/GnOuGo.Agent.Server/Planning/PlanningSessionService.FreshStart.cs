using GnOuGo.Flow.Core.Planning;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

public sealed partial class PlanningSessionService
{
    internal async Task<bool> CanStartWithCurrentSettingsAsync(PlanningSession state, CancellationToken ct)
    {
        if (state.Request.TenantId != Tenant || state.Status != PlanningStatus.Stopped || state.PendingCall is not null ||
            state.ModelCalls != 0 || state.Usage is { Calls: > 0 } || !state.Diagnostics.Any(d => d.Code == "LLM_BUDGET_EXCEEDED")) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Calls.AnyAsync(c => c.TenantId == Tenant && c.SessionId == state.Request.SessionId, ct)) return false;
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, state.Request.SessionId, EfPlanningSessionStore.Author, ct);
        if (saved is not null)
        {
            var usage = System.Text.Json.JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            if (usage is null || usage.Calls != 0 || usage.JournaledAttempts?.Count > 0) return false;
        }
        // Require absence of every exact session-owned issued request, receipt and HTTP attempt.
        foreach (var collection in new[] { PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection, PlanningModelJournal.HttpCollection })
            if ((await records.ListAsync(collection, Tenant, EfPlanningSessionStore.Author, ct))
                .Any(r => r.Key.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal))) return false;
        return true;
    }

    public async Task<PlanningSession> StartWithCurrentSettingsAsync(string id, LocalOperatorAccess access, CancellationToken ct)
    {
        _ = await access.GrantAsync();
        await using var lease = await PlanningSessionLease.TryAcquireAsync(contexts, Tenant, id, ct);
        if (lease is null) throw new PlanningConflictException("The original session still has an active owner.");
        var state = await GetAsync(id, ct) ?? throw new KeyNotFoundException();
        if (!await CanStartWithCurrentSettingsAsync(state, ct))
            throw new PlanningConflictException("A fresh start requires verified absence of dispatches and reservations. The original session is retained.");
        var selection = await access.ResolveAsync(null);
        return await StartAsync(state.Request.Name, state.Request.Prompt, state.Request.Options["host_save"] is not null, ct,
            failureEvidence: state.Request.FailureEvidence?.DeepClone().AsObject(),
            mode: state.Request.Mode, constructionPreset: CapturedPreset(state), thresholdMode: selection.Mode, diagnosticGrant: selection.Grant);
    }
}
