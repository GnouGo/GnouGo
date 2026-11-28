using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

// The campaign and the workflow reserve the same physical identity before send.
// Existing captured single-attempt accounts retain their previous logical settlement.
internal sealed class RuntimeBudgetJournal(ILLMHttpRetryJournal inner, JournaledModelBudget budget,
    LLMRequest request, int input, int output) : ILLMHttpRetryJournal
{
    private int _recorded;
    public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
    {
        var state = await inner.LoadAsync(ct);
        _recorded = state?.Attempts.Count ?? 0;
        if (state is not null)
            foreach (var attempt in state.Attempts)
                await ReserveAsync(budget, request, input, output, attempt.Id, ct);
        return state;
    }

    public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
    {
        if (state.Attempts.Count > _recorded)
        {
            try { await budget.Scope.EnsureAllowanceAsync(request, budget.Estimator, input, [output], budget.Stage, ct); }
            catch (WorkflowRuntimeException error) when (_recorded > 0 && error.Details is JsonObject details &&
                error.Code is ErrorCodes.LlmBudgetExceeded or ErrorCodes.LlmBudgetUnverifiable)
            {
                // The proposed physical send never started. Earlier confirmed attempts did.
                details["dispatch_status"] = "prior_attempts_recorded";
                details["next_attempt_started"] = false;
                details["attempt_count"] = _recorded;
                details["status_code"] = state.Attempts[_recorded - 1].Status;
                throw;
            }
            await inner.SaveAsync(state, ct);
            await ReserveAsync(budget, request, input, output, state.Attempts[^1].Id, ct);
            _recorded = state.Attempts.Count;
            return;
        }
        await inner.SaveAsync(state, ct);
    }

    internal static async Task RestoreAsync(JsonObject? record, LLMRequest request, JournaledModelBudget budget, LLMResponse response)
    {
        if (record is null) throw new InvalidOperationException("The runtime result has no campaign journal.");
        var state = record["transport"]?.Deserialize(LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        var last = state?.Attempts.LastOrDefault();
        if (record["effective_transport_policy"]?["mode"]?.ToString() != "runtime_confirmed_retry" &&
            (last is null || budget.Scope.Snapshot.JournaledAttempts?.ContainsKey(last.Id) != true)) return;
        if (state is null || last is not { Status: >= 200 and < 300, Failure: null })
            throw new InvalidOperationException("The runtime result has no confirmed physical completion.");
        var input = checked((int)record["input_ceiling"]!.GetValue<long>());
        var output = checked((int)record["output_ceiling"]!.GetValue<long>());
        foreach (var attempt in state.Attempts)
            await ReserveAsync(budget, request, input, output, attempt.Id, CancellationToken.None);
        if (response.Usage is not null)
            await budget.Scope.SettleJournaledAttemptAsync(last.Id, request, budget.Estimator, response.Usage, budget.Stage, CancellationToken.None);
    }

    private static Task ReserveAsync(JournaledModelBudget budget, LLMRequest request, int input, int output, string id, CancellationToken ct)
        => budget.Scope.ReserveJournaledAttemptAsync(id, request, budget.Estimator, input, output, [], budget.Stage, ct);
}
