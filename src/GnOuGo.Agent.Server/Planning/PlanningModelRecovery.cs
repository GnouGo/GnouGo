using System.Text.Json;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Explicit operator retry. Retains the old request and budgets its unknown usage.</summary>
internal static class PlanningModelRecovery
{
    internal static async Task ResumeAsync(PlanningSession state, IKeyVaultRecordStore records, string? requestId, CancellationToken ct, LLMUsageBudgetLimits? limits = null)
    {
        if (state.Status != PlanningStatus.Stopped && !(state.RequiresPlanningRevision && state.Status == PlanningStatus.Generating) ||
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
        if (state.RequiresPlanningRevision)
        {
            // The original envelope remains in the journal. Settle its accounting,
            // but never apply an obsolete proposal under current planning semantics.
            state.PendingCall = null;
            ClearTransportDiagnostics(state);
            state.Status = PlanningStatus.Stopped;
            if (!state.Diagnostics.Any(d => d.Code == "PLANNING_REVISION_REQUIRED"))
                state.Diagnostics.Add(new("PLANNING_REVISION_REQUIRED", "/", "The saved response was accounted for. Explicitly revise this retired planning contract before continuing."));
            return;
        }
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
        if (usage.Calls != state.ModelCalls || state.Usage is { } previous &&
            (usage.InputTokens < previous.InputTokens || usage.OutputTokens < previous.OutputTokens || usage.EstimatedCost < previous.EstimatedCost))
            throw new PlanningConflictException("The dispatch accounting changed; reconcile it before resuming.");
        try { _ = new LLMUsageBudgetScope(limits ?? new() { MaxCalls = state.Request.MaxModelCalls }, usage); }
        catch (ArgumentException) { throw new PlanningConflictException("The durable dispatch accounting is invalid for this host."); }
        state.Usage = usage;
    }

    internal static async Task PrepareAsync(PlanningSession state, IKeyVaultRecordStore records,
        LLMUsageBudgetLimits limits, IModelUsageCostEstimator estimator, IExchangeRateProvider exchangeRates, CancellationToken ct)
    {
        if (state.Status != PlanningStatus.Stopped || state.PendingCall is not { } pending)
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
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, tenant, session, author, ct)
            ?? throw new PlanningConflictException("The dispatch budget is unavailable; recovery cannot reset it.");
        var current = JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (current.Calls != state.ModelCalls)
            throw new PlanningConflictException("The dispatch accounting changed; reconcile it before retrying.");
        var correctionKey = key + ":unreported-usage";
        var recorded = await records.GetAsync(PlanningBudgetSink.Collection, tenant, correctionKey, author, ct);
        LLMUsageBudgetSnapshot corrected;
        if (recorded is null)
        {
            var scope = new LLMUsageBudgetScope(limits, current, exchangeRateProvider: exchangeRates);
            // Treat every serialized request byte as an input token, at least the configured
            // input allowance, plus the entire enforced output allowance. This is an estimate,
            // never a fabricated provider receipt or a claim that the failed call was free.
            var input = Math.Max(state.Request.Generation.MaxInputTokensPerRequest,
                JsonSerializer.SerializeToUtf8Bytes(pending.Request, PlanningJsonContext.Default.LLMRequest).LongLength);
            var output = pending.Request.RequireOutputTokenLimit && pending.Request.MaxTokens is > 0
                ? pending.Request.MaxTokens.Value : throw new PlanningConflictException("The failed request has no enforced output bound.");
            try { await scope.AccountUnreportedUsageAsync(pending.Request, estimator, input, output, ct); }
            catch (WorkflowRuntimeException ex) when (ex.Code == ErrorCodes.LlmBudgetExceeded) { /* Persist the charge even when it exhausts the allowance. */ }
            corrected = scope.Snapshot;
            // Write the absolute correction first. A restart before the session checkpoint
            // reuses it instead of adding the estimate twice.
            await records.UpsertAsync(PlanningBudgetSink.Collection, tenant, correctionKey,
                JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), author, ct);
        }
        else corrected = JsonSerializer.Deserialize(recorded.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (corrected.Calls != current.Calls || corrected.InputTokens < current.InputTokens || corrected.OutputTokens < current.OutputTokens ||
            corrected.EstimatedCostCurrency != current.EstimatedCostCurrency || corrected.EstimatedCost < current.EstimatedCost)
            throw new PlanningConflictException("A recovery correction cannot replace newer usage.");
        await records.UpsertAsync(PlanningBudgetSink.Collection, tenant, session,
            JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), author, ct);
        state.Usage = corrected;
        if (state.ModelCalls >= Math.Min(state.Request.MaxModelCalls, limits.MaxCalls ?? int.MaxValue) ||
            limits.MaxTotalTokens is { } tokens && corrected.TotalTokens >= tokens ||
            limits.MaxEstimatedCost is { } cost && corrected.EstimatedCost >= cost.Amount ||
            pending.Purpose == "replan" && state.ReplanAttempts >= state.Request.MaxReplanAttempts)
        {
            state.Diagnostics = [new(ErrorCodes.LlmBudgetExceeded, "$", "Recovery retained the failed dispatch and its estimated usage; no retry allowance remains.")];
            return;
        }
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        request.ClientRequestId = null;
        var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        request.ClientRequestId = session + ":" + (++state.ModelCalls) + ":" + hash;
        if (pending.Purpose == "replan") state.ReplanAttempts++;
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = pending.Purpose, Request = request };
        state.Status = PlanningStatus.Generating; ClearTransportDiagnostics(state); state.Yaml = null; state.ApprovedHash = null;
    }

    private static void ClearTransportDiagnostics(PlanningSession state) => state.Diagnostics.RemoveAll(d =>
        d.Code is ErrorCodes.LlmBudgetUnverifiable or "MODEL_DISPATCH_UNVERIFIABLE" or "PLANNING_HOST_FAILURE");
}
