using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Agent.Shared;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Planning;

public sealed partial class PlanningSessionService
{
    private const string Cancellations = "agent-planning-cancellations-v1";

    private async Task<PlanningSession> RequestCancellationAsync(string id, PlanningCommand command, CancellationToken ct)
    {
        var current = await store.LoadAsync(Tenant, id, ct) ?? throw new KeyNotFoundException("Planning session not found.");
        if (current.Revision != command.ExpectedRevision) throw new PlanningConflictException("The session changed; reload its current revision.");
        if (current.Status is PlanningStatus.Saved or PlanningStatus.Saving or PlanningStatus.Cancelled)
            throw new PlanningConflictException("This planning session is closed.");
        var request = new JsonObject { ["revision"] = current.Revision, ["requestId"] = current.PendingCall?.Id };
        await records.UpsertAsync(Cancellations, Tenant, id, request.ToJsonString(), EfPlanningSessionStore.Author, ct);
        _queue.Writer.TryWrite(id);
        // Cancellation is the only command allowed to signal an owner without its lease.
        // It cannot modify the owner's session or discard a completed response.
        await using var lease = await PlanningSessionLease.TryAcquireAsync(contexts, Tenant, id, ct);
        if (lease is null) return current;
        current = await store.LoadAsync(Tenant, id, ct) ?? throw new KeyNotFoundException("Planning session not found.");
        if (!await IsCancellationRequestedAsync(current, ct)) throw new PlanningConflictException("The session changed before cancellation was applied.");
        return await AdvanceAsync(current, new() { Kind = "cancel", ExpectedRevision = current.Revision }, ct);
    }

    private async Task<bool> IsCancellationRequestedAsync(PlanningSession state, CancellationToken ct)
    {
        var saved = await records.GetAsync(Cancellations, Tenant, state.Request.SessionId, EfPlanningSessionStore.Author, ct);
        if (saved is null) return false;
        var request = JsonNode.Parse(saved.Value)!;
        var revision = request["revision"]!.GetValue<long>();
        return state.Revision >= revision && state.Status is not (PlanningStatus.Saved or PlanningStatus.Cancelled);
    }

    private async Task ObserveCancellationAsync(string id, CancellationTokenSource interrupt, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                var state = await store.LoadAsync(Tenant, id, ct);
                if (state is not null && await IsCancellationRequestedAsync(state, ct))
                {
                    await interrupt.CancelAsync();
                    return;
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task<PlanningRequestInspectionDto?> InspectRequestAsync(PlanningSession state, CancellationToken ct)
    {
        if (state.Request.TenantId != Tenant) throw new PlanningConflictException("The session belongs to another tenant.");
        var cancelled = await IsCancellationRequestedAsync(state, ct);
        await using var lease = await PlanningSessionLease.TryAcquireAsync(contexts, Tenant, state.Request.SessionId, ct);
        if (lease is null) return new("active", state.PendingCall?.Id, cancelled);
        if (state.PendingCall is not { } pending) return cancelled ? new("idle", null, true) : null;
        if (state.Diagnostics.Any(d => d.Code == ErrorCodes.ModelRequestRejected)) return new("provider_rejection", pending.Id, cancelled);
        var receipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, state.Request.SessionId + ":" + pending.Id, EfPlanningSessionStore.Author, ct);
        return new(receipt is null ? "completion_unknown" : "receipt_available", pending.Id, cancelled);
    }

    internal async Task<PlanningSessionDto> ToDtoAsync(PlanningSession state, CancellationToken ct)
    {
        var dto = PlanningEndpoints.ToDto(state) with { RequestInspection = await InspectRequestAsync(state, ct) };
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, state.Request.SessionId, EfPlanningSessionStore.Author, ct);
        var usage = saved is null ? null : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        return usage is null ? dto : dto with { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens,
            EstimatedCost = usage.EstimatedCost, Currency = usage.EstimatedCostCurrency };
    }
}
