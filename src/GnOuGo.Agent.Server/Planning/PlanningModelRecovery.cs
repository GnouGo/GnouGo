using System.Text.Json;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Recovers exact saved receipts or durably recorded eligible HTTP attempts; never replays uncertainty.</summary>
internal static class PlanningModelRecovery
{
    internal static async Task ResumeAsync(PlanningSession state, IKeyVaultRecordStore records, string? requestId, CancellationToken ct, LLMUsageBudgetLimits? limits = null)
    {
        if (state.SchemaVersion != 11 || state.Status != PlanningStatus.Stopped ||
            state.PendingCall is not { } pending || requestId != pending.Id ||
            state.Diagnostics.Any(d => d.Code == ErrorCodes.ModelRequestRejected))
            throw new PlanningConflictException("Resume must identify the stopped request with a saved completion response.");
        var tenant = state.Request.TenantId;
        var key = state.Request.SessionId + ":" + pending.Id;
        var issued = await records.GetAsync(PlanningModelJournal.RequestCollection, tenant, key, EfPlanningSessionStore.Author, ct);
        var receipt = await records.GetAsync(PlanningModelJournal.Collection, tenant, key, EfPlanningSessionStore.Author, ct);
        if (issued is null || receipt is null) throw new PlanningConflictException("The original request and completion receipt must both be available. Nothing was dispatched.");
        var original = JsonSerializer.Deserialize(issued.Value, PlanningJsonContext.Default.LLMRequest);
        if (original is null || original.ClientRequestId != pending.Id ||
            JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest) != JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest))
            throw new PlanningConflictException("The saved request identity or schema differs from the pending request.");
        original.ClientRequestId = null;
        var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
        if (!pending.Id.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal) || !pending.Id.EndsWith(":" + hash, StringComparison.Ordinal))
            throw new PlanningConflictException("The original request fingerprint is invalid.");
        var response = JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)
            ?? throw new PlanningConflictException("The completion receipt is invalid.");
        var json = response.Json ?? System.Text.Json.Nodes.JsonNode.Parse(response.Text);
        if (json is null || original.StructuredOutputSchema is null ||
            PlanningContractValidation.ValidateInstanceFindings(json, original.StructuredOutputSchema).Count != 0)
            throw new PlanningConflictException("The saved response does not satisfy its original issued schema.");
        await RestoreUsageAsync(state, records, ct, limits);
        state.Status = PlanningStatus.Generating;
        ClearTransportDiagnostics(state);
        state.ApprovedHash = null;
    }

    private static async Task RestoreUsageAsync(PlanningSession state, IKeyVaultRecordStore records, CancellationToken ct, LLMUsageBudgetLimits? limits)
    {
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, state.Request.TenantId, state.Request.SessionId, EfPlanningSessionStore.Author, ct)
            ?? throw new PlanningConflictException("The durable dispatch accounting is unavailable.");
        var usage = JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)
            ?? throw new PlanningConflictException("The durable dispatch accounting is invalid.");
        if (PlanningTransportPolicies.Read(state.Request) is null || usage.JournaledAttempts is null ||
            usage.JournaledAttempts.Count != usage.Calls || usage.Calls < state.ModelCalls ||
            usage.JournaledAttempts.Keys.Any(id => !id.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal)) ||
            state.Usage is { } earlier && (usage.Calls < earlier.Calls || earlier.JournaledAttempts is { } retained &&
                retained.Any(pair => !usage.JournaledAttempts.TryGetValue(pair.Key, out var actual) ||
                    pair.Value.InputTokenCeiling != actual.InputTokenCeiling || pair.Value.OutputTokenCeiling != actual.OutputTokenCeiling ||
                    pair.Value.ReservedEstimatedCost != actual.ReservedEstimatedCost ||
                    pair.Value.VerifiedUsage is not null && pair.Value.VerifiedUsage != actual.VerifiedUsage)))
            throw new PlanningConflictException("The dispatch accounting changed; reconcile it before resuming.");
        try { _ = new LLMUsageBudgetScope(limits ?? new() { MaxCalls = state.Request.MaxModelCalls }, usage); }
        catch (ArgumentException) { throw new PlanningConflictException("The durable dispatch accounting is invalid for this host."); }
        state.Usage = usage;
    }

    internal static async Task PrepareAsync(PlanningSession state, IKeyVaultRecordStore records,
        LLMUsageBudgetLimits limits, IModelUsageCostEstimator estimator, IExchangeRateProvider exchangeRates, CancellationToken ct)
    {
        if (state.SchemaVersion != 11 || state.Status != PlanningStatus.Stopped || state.PendingCall is not { } pending)
            throw new PlanningConflictException("Only a stopped pending model request can be retried.");
        if (state.Diagnostics.Any(d => d.Code == ErrorCodes.ModelRequestRejected))
            throw new PlanningConflictException("The provider rejected this request. Correct the request or provider configuration, then start a new planning session.");
        var tenant = state.Request.TenantId; var session = state.Request.SessionId;
        var key = session + ":" + pending.Id;
        var author = EfPlanningSessionStore.Author;
        if (await records.GetAsync(PlanningModelJournal.Collection, tenant, key, author, ct) is not null)
        {
            // A late durable receipt is replayed under the original identity without another charge.
            await RestoreUsageAsync(state, records, ct, limits);
            state.Status = PlanningStatus.Generating; ClearTransportDiagnostics(state); return;
        }
        if (PlanningTransportPolicies.Read(state.Request) is null)
            throw new PlanningConflictException("The captured transport policy is missing; no request can be dispatched.");
        var issued = await records.GetAsync(PlanningModelJournal.RequestCollection, tenant, key, author, ct);
        if (issued?.Value != JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest))
            throw new PlanningConflictException("The exact issued planning request is unavailable for HTTP recovery.");
        await RestoreUsageAsync(state, records, ct, limits);
        var journal = new PlanningHttpModelJournal(records, new LLMUsageBudgetScope(limits, state.Usage, exchangeRateProvider: exchangeRates),
            state.Request, pending.Request, estimator, PlanningModelJournal.HttpCollection, author,
            DateTimeOffset.UtcNow + (PlanningBudgetOptions.Parse(state.Request.Options)?.MaxElapsed ?? TimeSpan.FromHours(5)) -
            TimeSpan.FromMilliseconds(state.ActiveMilliseconds));
        await journal.VerifyRecoveryAsync(ct);
        state.Status = PlanningStatus.Generating;
        ClearTransportDiagnostics(state);
    }

    private static void ClearTransportDiagnostics(PlanningSession state) => state.Diagnostics.RemoveAll(d =>
        d.Code is ErrorCodes.LlmBudgetUnverifiable or "MODEL_DISPATCH_UNVERIFIABLE" or "PLANNING_HOST_FAILURE");
}
