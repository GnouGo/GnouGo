using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>One physical request's conservative reservation in its owning budget's currency.</summary>
public sealed record LLMJournaledAttempt(int InputTokenCeiling, int OutputTokenCeiling,
    decimal ReservedEstimatedCost, decimal ReservedEstimatedCostUsd, LLMJournaledAttemptUsage? VerifiedUsage = null);

/// <summary>Verified provider usage replacing only that physical request's reservation.</summary>
public sealed record LLMJournaledAttemptUsage(long InputTokens, long OutputTokens, long TotalTokens,
    decimal EstimatedCost, decimal EstimatedCostUsd, string UsageHash);

public sealed partial class LLMUsageBudgetScope
{
    /// <summary>
    /// Durably charges a physical attempt before dispatch. The host must first journal its
    /// immutable request identity and must not redispatch uncertain or already-issued attempts.
    /// Remaining reviews are admitted, but only this attempt is charged. Persistence failures
    /// retain conservative charges; this API never refunds an unverified attempt.
    /// </summary>
    public async Task ReserveJournaledAttemptAsync(string id, LLMRequest request, IModelUsageCostEstimator? estimator,
        int inputTokenCeiling, int outputTokenCeiling, IReadOnlyList<int> remainingReviewOutputs,
        string stage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(remainingReviewOutputs);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokenCeiling);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokenCeiling);
        if (remainingReviewOutputs.Any(output => output < 0))
            throw new ArgumentException("Review output ceilings must be nonnegative.", nameof(remainingReviewOutputs));
        ct.ThrowIfCancellationRequested();
        using var warnings = ObserveTokenWarnings(request.ClientRequestId);
        var safeStage = SanitizeStage(stage);
        var scopes = JournaledScopes();
        var gates = new List<SemaphoreSlim>();
        try
        {
            await AcquireJournaledCostGatesAsync(scopes, gates, ct).ConfigureAwait(false);
            var outputs = new[] { outputTokenCeiling }.Concat(remainingReviewOutputs).ToArray();
            var prepared = new List<(LLMJournaledAttempt Entry, decimal Allowance)>();
            foreach (var scope in scopes)
            {
                if (scope.Snapshot.JournaledAttempts?.TryGetValue(id, out var retained) == true)
                {
                    scope.ValidateJournaledBounds(retained, inputTokenCeiling, outputTokenCeiling, safeStage);
                    prepared.Add((retained, 0));
                    continue;
                }
                var cost = await scope.JournaledCostAsync(request, estimator, inputTokenCeiling, outputTokenCeiling, safeStage, ct).ConfigureAwait(false);
                var allowance = await scope.EnsureLocalAllowanceAsync(request, estimator, inputTokenCeiling, outputs, safeStage, ct).ConfigureAwait(false);
                prepared.Add((new(inputTokenCeiling, outputTokenCeiling, cost.Amount, cost.Usd), allowance));
            }
            UpdateJournaledScopes(scopes, () =>
            {
                var snapshots = new List<LLMUsageBudgetSnapshot>();
                for (var index = 0; index < scopes.Count; index++)
                {
                    var scope = scopes[index];
                    var current = scope._snapshot;
                    if (current.JournaledAttempts?.TryGetValue(id, out var retained) == true)
                    {
                        scope.ValidateJournaledBounds(retained, inputTokenCeiling, outputTokenCeiling, safeStage);
                        snapshots.Add(current);
                        continue;
                    }
                    var (entry, allowance) = prepared[index];
                    scope.EnsureLocalAllowanceCore(current, inputTokenCeiling, outputs, allowance, safeStage);
                    snapshots.Add(current with
                    {
                        Calls = checked(current.Calls + 1),
                        InputTokens = checked(current.InputTokens + inputTokenCeiling),
                        OutputTokens = checked(current.OutputTokens + outputTokenCeiling),
                        TotalTokens = checked(current.TotalTokens + (long)inputTokenCeiling + outputTokenCeiling),
                        EstimatedCost = checked(current.EstimatedCost + entry.ReservedEstimatedCost),
                        EstimatedCostUsd = checked(current.EstimatedCostUsd + entry.ReservedEstimatedCostUsd),
                        JournaledAttempts = WithJournaledAttempt(current, id, entry)
                    });
                }
                for (var index = 0; index < scopes.Count; index++) scopes[index]._snapshot = snapshots[index];
            });
            await PersistJournaledScopesAsync(scopes, safeStage, ct).ConfigureAwait(false);
        }
        finally
        {
            for (var index = gates.Count - 1; index >= 0; index--) gates[index].Release();
        }
    }

    /// <summary>
    /// Settles verified usage exactly once, without incrementing calls. Failed/uncertain
    /// attempts must not use this API. Actual overages remain charged and fail closed.
    /// </summary>
    public async Task SettleJournaledAttemptAsync(string id, LLMRequest request, IModelUsageCostEstimator? estimator,
        JsonNode usage, string stage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(usage);
        using var warnings = ObserveTokenWarnings(request.ClientRequestId);
        var safeStage = SanitizeStage(stage);
        ParsedUsage? parsed;
        try { parsed = ParseUsage(usage); }
        catch (OverflowException) { throw CreateUnverifiable(safeStage, "usage", Snapshot); }
        if (parsed?.InputTokens is not { } input || parsed.OutputTokens is not { } output)
            throw CreateUnverifiable(safeStage, "usage", Snapshot);
        var usageHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(usage.ToJsonString())));
        var scopes = JournaledScopes();
        var gates = new List<SemaphoreSlim>();
        // A known response's accounting must survive cancellation of its caller.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ct = completion.Token;
        try
        {
            await AcquireJournaledCostGatesAsync(scopes, gates, ct).ConfigureAwait(false);
            var settled = new List<LLMJournaledAttemptUsage>();
            foreach (var scope in scopes)
            {
                if (scope.Snapshot.JournaledAttempts is not { } attempts || !attempts.TryGetValue(id, out var entry))
                    throw scope.CreateUnverifiable(safeStage, "journaled_attempt", scope.Snapshot);
                if (entry.VerifiedUsage is { } existing)
                {
                    if (existing.UsageHash != usageHash)
                        throw scope.CreateUnverifiable(safeStage, "journaled_usage", scope.Snapshot);
                    settled.Add(existing);
                    continue;
                }
                var cost = await scope.JournaledCostAsync(request, estimator, input, output, safeStage, ct).ConfigureAwait(false);
                settled.Add(new(input, output, parsed.TotalTokens, cost.Amount, cost.Usd, usageHash));
            }
            UpdateJournaledScopes(scopes, () =>
            {
                var snapshots = new List<LLMUsageBudgetSnapshot>();
                for (var index = 0; index < scopes.Count; index++)
                {
                    var scope = scopes[index];
                    var current = scope._snapshot;
                    var entry = current.JournaledAttempts![id];
                    var actual = settled[index];
                    if (entry.VerifiedUsage is { } existing)
                    {
                        if (existing != actual) throw scope.CreateUnverifiable(safeStage, "journaled_usage", current);
                        snapshots.Add(current);
                        continue;
                    }
                    snapshots.Add(current with
                    {
                        InputTokens = checked(current.InputTokens - entry.InputTokenCeiling + actual.InputTokens),
                        OutputTokens = checked(current.OutputTokens - entry.OutputTokenCeiling + actual.OutputTokens),
                        TotalTokens = checked(current.TotalTokens - entry.InputTokenCeiling - entry.OutputTokenCeiling + actual.TotalTokens),
                        EstimatedCost = checked(current.EstimatedCost - entry.ReservedEstimatedCost + actual.EstimatedCost),
                        EstimatedCostUsd = checked(current.EstimatedCostUsd - entry.ReservedEstimatedCostUsd + actual.EstimatedCostUsd),
                        JournaledAttempts = WithJournaledAttempt(current, id, entry with { VerifiedUsage = actual })
                    });
                }
                for (var index = 0; index < scopes.Count; index++) scopes[index]._snapshot = snapshots[index];
            });
            foreach (var scope in scopes.Where(scope => scope._advisoryTokens)) scope.WarnJournaledSettlementThresholds(id, safeStage);
            await PersistJournaledScopesAsync(scopes, safeStage, ct).ConfigureAwait(false);
            foreach (var scope in scopes) scope.EnsureJournaledSettlementBounds(id, safeStage);
        }
        finally
        {
            for (var index = gates.Count - 1; index >= 0; index--) gates[index].Release();
        }
    }

    private async Task<(decimal Amount, decimal Usd)> JournaledCostAsync(LLMRequest request,
        IModelUsageCostEstimator? estimator, long input, long output, string stage, CancellationToken ct)
    {
        if (_costLimit is null) return (0, 0);
        ModelUsageCostEstimate? source;
        try { source = estimator?.EstimateCostWithCurrency(request.Model, input, output, request.Provider); }
        catch { throw CreateUnverifiable(stage, "pricing", Snapshot); }
        if (source is null) throw CreateUnverifiable(stage, "pricing", Snapshot);
        var cost = await ConvertToBudgetCurrencyAsync(source, stage, ct).ConfigureAwait(false);
        return (cost.Amount, ResolveLegacyUsdIncrement(source, cost));
    }

    private void ValidateJournaledBounds(LLMJournaledAttempt entry, int input, int output, string stage)
    {
        if (entry.InputTokenCeiling != input || entry.OutputTokenCeiling != output)
            throw CreateUnverifiable(stage, "journaled_reservation", Snapshot);
    }

    private void EnsureJournaledSettlementBounds(string id, string stage)
    {
        var snapshot = Snapshot;
        var entry = snapshot.JournaledAttempts![id];
        var usage = entry.VerifiedUsage!;
        var ceiling = (long)entry.InputTokenCeiling + entry.OutputTokenCeiling;
        if (usage.InputTokens > entry.InputTokenCeiling && !WarnTokenThreshold(stage, "reserved_input_tokens", entry.InputTokenCeiling, usage.InputTokens))
            throw CreateExceeded(stage, "reserved_input_tokens", entry.InputTokenCeiling, usage.InputTokens, snapshot);
        if (usage.OutputTokens > entry.OutputTokenCeiling && !WarnTokenThreshold(stage, "reserved_output_tokens", entry.OutputTokenCeiling, usage.OutputTokens))
            throw CreateExceeded(stage, "reserved_output_tokens", entry.OutputTokenCeiling, usage.OutputTokens, snapshot);
        if (usage.TotalTokens > ceiling && !WarnTokenThreshold(stage, "reserved_tokens", ceiling, usage.TotalTokens))
            throw CreateExceeded(stage, "reserved_tokens", ceiling, usage.TotalTokens, snapshot);
        if (usage.EstimatedCost > entry.ReservedEstimatedCost || usage.EstimatedCostUsd > entry.ReservedEstimatedCostUsd)
            throw CreateExceeded(stage, "reserved_cost", entry.ReservedEstimatedCost, usage.EstimatedCost, snapshot);
        if (Limits.MaxTotalTokens is { } tokens && snapshot.TotalTokens > tokens && !WarnTokenThreshold(stage, "total_tokens", tokens, snapshot.TotalTokens))
            throw CreateExceeded(stage, "total_tokens", tokens, snapshot.TotalTokens, snapshot);
        if (_costLimit is { } cost && snapshot.EstimatedCost > cost.Amount)
            throw CreateExceeded(stage, ResolveEstimatedCostLimitKind(), cost.Amount, snapshot.EstimatedCost, snapshot);
    }

    private void WarnJournaledSettlementThresholds(string id, string stage)
    {
        var snapshot = Snapshot;
        var entry = snapshot.JournaledAttempts![id];
        var usage = entry.VerifiedUsage!;
        var ceiling = (long)entry.InputTokenCeiling + entry.OutputTokenCeiling;
        if (usage.InputTokens > entry.InputTokenCeiling) WarnTokenThreshold(stage, "reserved_input_tokens", entry.InputTokenCeiling, usage.InputTokens);
        if (usage.OutputTokens > entry.OutputTokenCeiling) WarnTokenThreshold(stage, "reserved_output_tokens", entry.OutputTokenCeiling, usage.OutputTokens);
        if (usage.TotalTokens > ceiling) WarnTokenThreshold(stage, "reserved_tokens", ceiling, usage.TotalTokens);
        if (Limits.MaxTotalTokens is { } tokens && snapshot.TotalTokens > tokens) WarnTokenThreshold(stage, "total_tokens", tokens, snapshot.TotalTokens);
    }

    private List<LLMUsageBudgetScope> JournaledScopes()
    {
        var scopes = new List<LLMUsageBudgetScope>();
        for (var scope = this; scope is not null; scope = scope._parent) scopes.Add(scope);
        scopes.Reverse();
        return scopes;
    }

    private static async Task AcquireJournaledCostGatesAsync(IEnumerable<LLMUsageBudgetScope> scopes,
        List<SemaphoreSlim> acquired, CancellationToken ct)
    {
        foreach (var scope in scopes)
            if (scope._unaccountedCostGate is { } gate)
            { await gate.WaitAsync(ct).ConfigureAwait(false); acquired.Add(gate); }
    }

    private static void UpdateJournaledScopes(IReadOnlyList<LLMUsageBudgetScope> scopes, Action update, int index = 0)
    {
        if (index == scopes.Count) { update(); return; }
        lock (scopes[index]._gate) UpdateJournaledScopes(scopes, update, index + 1);
    }

    private static async Task PersistJournaledScopesAsync(IEnumerable<LLMUsageBudgetScope> scopes, string stage, CancellationToken ct)
    {
        Exception? failure = null;
        foreach (var scope in scopes)
            try { await scope.PersistAsync(scope.Snapshot, stage, ct).ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; }
        if (failure is not null) throw failure;
    }

    private static IReadOnlyDictionary<string, LLMJournaledAttempt> WithJournaledAttempt(
        LLMUsageBudgetSnapshot snapshot, string id, LLMJournaledAttempt entry)
    {
        var values = new Dictionary<string, LLMJournaledAttempt>(snapshot.JournaledAttempts ??
            new Dictionary<string, LLMJournaledAttempt>(), StringComparer.Ordinal) { [id] = entry };
        return new ReadOnlyDictionary<string, LLMJournaledAttempt>(values);
    }

    private static IReadOnlyDictionary<string, LLMJournaledAttempt>? ValidateJournaledAttempts(LLMUsageBudgetSnapshot snapshot)
    {
        if (snapshot.JournaledAttempts is null) return null;
        long input = 0, output = 0, total = 0;
        decimal cost = 0, usd = 0;
        try
        {
            foreach (var (id, entry) in snapshot.JournaledAttempts)
            {
                if (string.IsNullOrWhiteSpace(id) || entry is null || entry.InputTokenCeiling < 0 || entry.OutputTokenCeiling < 0 ||
                    entry.ReservedEstimatedCost < 0 || entry.ReservedEstimatedCostUsd < 0)
                    throw new ArgumentException("The initial journaled LLM reservation is invalid.", nameof(snapshot));
                var usage = entry.VerifiedUsage;
                if (usage is not null && (usage.InputTokens < 0 || usage.OutputTokens < 0 ||
                    usage.TotalTokens < checked(usage.InputTokens + usage.OutputTokens) || usage.EstimatedCost < 0 || usage.EstimatedCostUsd < 0 ||
                    usage.UsageHash is null || usage.UsageHash.Length != 64 || usage.UsageHash.Any(character => !char.IsAsciiHexDigit(character))))
                    throw new ArgumentException("The initial journaled LLM usage is invalid.", nameof(snapshot));
                input = checked(input + (usage?.InputTokens ?? entry.InputTokenCeiling));
                output = checked(output + (usage?.OutputTokens ?? entry.OutputTokenCeiling));
                total = checked(total + (usage?.TotalTokens ?? (long)entry.InputTokenCeiling + entry.OutputTokenCeiling));
                cost = checked(cost + (usage?.EstimatedCost ?? entry.ReservedEstimatedCost));
                usd = checked(usd + (usage?.EstimatedCostUsd ?? entry.ReservedEstimatedCostUsd));
            }
        }
        catch (OverflowException exception) { throw new ArgumentException("The initial journaled LLM accounting overflowed.", nameof(snapshot), exception); }
        if (snapshot.JournaledAttempts.Count > snapshot.Calls || input > snapshot.InputTokens || output > snapshot.OutputTokens ||
            total > snapshot.TotalTokens || cost > snapshot.EstimatedCost || usd > snapshot.EstimatedCostUsd)
            throw new ArgumentException("The initial journaled LLM accounting exceeds its cumulative snapshot.", nameof(snapshot));
        return new ReadOnlyDictionary<string, LLMJournaledAttempt>(new Dictionary<string, LLMJournaledAttempt>(snapshot.JournaledAttempts, StringComparer.Ordinal));
    }
}
