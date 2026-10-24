using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

/// <summary>Admission and encrypted evidence only. All retry decisions belong to AI.Core.</summary>
internal sealed class BenchmarkHttpJournal(BenchmarkCampaign campaign, string requestId, long inputCeiling, long outputCeiling, decimal costCeiling,
    int? sessionAttemptLimit = 8, decimal reviewCostHeadroom = 0, int reviewAttemptHeadroom = 0,
    JsonObject? effectiveTransportPolicy = null, TimeProvider? time = null) : ILLMHttpRetryJournal
{
    private readonly JsonObject? _effectiveTransportPolicy = effectiveTransportPolicy?.DeepClone().AsObject();
    internal const string Collection = "planning-evaluation-http-attempts";
    internal static bool IsRuntimeSingleAttempt(JsonObject? policy) => policy?["version"]?.GetValue<int>() == 2 &&
        policy["mode"]?.ToString() is "runtime_single_attempt" or "sdk_proxy_single_attempt";

    internal static JsonObject UndispatchedUsage() => new()
    {
        ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["benchmark_cost_eur"] = 0m,
        ["transport_attempts"] = 0, ["uncertain_attempts"] = 0, ["reserved_input_tokens"] = 0L,
        ["reserved_output_tokens"] = 0L, ["reserved_cost_eur"] = 0m, ["benchmark_usage_bounded"] = true
    };
    internal async Task PrepareAsync(CancellationToken ct, JsonObject? exchangeQuote = null)
    {
        if (await LoadAsync(ct) is null) await SaveAsync(new(), ct, exchangeQuote);
    }
    public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
        => (await campaign.LoadAsync(Collection, requestId, ct))?["transport"] is { } state
            ? JsonSerializer.Deserialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState) : null;

    public Task SaveAsync(LLMHttpRetryState state, CancellationToken ct) => SaveAsync(state, ct, null);

    private async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct, JsonObject? exchangeQuote)
    {
        ct.ThrowIfCancellationRequested();
        var existing = await campaign.LoadAsync(Collection, requestId, ct);
        if (existing is not null && !JsonNode.DeepEquals(existing["effective_transport_policy"], _effectiveTransportPolicy))
            throw new InvalidOperationException("An issued effective transport policy cannot change during recovery.");
        if (existing?["effective_transport_policy"] is JsonObject captured &&
            existing["effective_transport_policy_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(captured.ToJsonString()))
            throw new InvalidOperationException("The captured effective transport policy hash changed.");
        if (sessionAttemptLimit is <= 0 || existing is not null &&
            (existing.ContainsKey("session_attempt_limit") ? existing["session_attempt_limit"]?.GetValue<int>() : 8) != sessionAttemptLimit)
            throw new InvalidOperationException("An issued HTTP attempt limit cannot change during recovery.");
        if (reviewCostHeadroom < 0 || reviewAttemptHeadroom is < 0 or > 2 || existing is not null &&
            ((existing["construction_review_cost_headroom_eur"]?.GetValue<decimal>() ?? 0) != reviewCostHeadroom ||
             (existing["construction_review_attempt_headroom"]?.GetValue<int>() ?? 0) != reviewAttemptHeadroom))
            throw new InvalidOperationException("Issued construction review headroom cannot change during recovery.");
        var record = existing?.DeepClone().AsObject() ?? new JsonObject
        {
            ["input_ceiling"] = inputCeiling, ["output_ceiling"] = outputCeiling, ["cost_ceiling_eur"] = costCeiling, ["session_attempt_limit"] = sessionAttemptLimit
        };
        if (existing is null && reviewAttemptHeadroom > 0)
        {
            record["construction_review_cost_headroom_eur"] = reviewCostHeadroom;
            record["construction_review_attempt_headroom"] = reviewAttemptHeadroom;
        }
        if (inputCeiling <= 0 || outputCeiling <= 0 || costCeiling < 0) throw new InvalidOperationException("Conservative attempt limits are required.");
        if (existing is null && exchangeQuote is not null) record["exchange_quote"] = exchangeQuote.DeepClone();
        if (existing is null && _effectiveTransportPolicy is not null)
        {
            record["effective_transport_policy"] = _effectiveTransportPolicy.DeepClone();
            record["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(_effectiveTransportPolicy.ToJsonString());
        }
        record["transport"] = JsonSerializer.SerializeToNode(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        var oldCount = existing?["transport"]?["Attempts"]?.AsArray().Count ?? 0;
        if (state.Attempts.Count > oldCount)
        {
            if (state.Attempts.Count != oldCount + 1 || state.Attempts[^1].Status is not null)
                throw new InvalidOperationException("Each new dispatch must have one reserved identity.");
            var totals = await AccountingAsync(campaign, requestId, record, ct);
            var sessionExhausted = sessionAttemptLimit is { } limit && totals["session_calls"]!.GetValue<long>() + reviewAttemptHeadroom > limit;
            if (PlanningBenchmarkMeasurements.RetainsAllHttpFailures(record))
            {
                if (_effectiveTransportPolicy?["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() is not (>= 1 and <= 3) ||
                    state.Attempts.Count > _effectiveTransportPolicy["effective_retry_policy"]!["MaxAttempts"]!.GetValue<int>())
                    throw new InvalidOperationException("The captured request attempt allowance is exhausted.");
                if (IsRuntimeSingleAttempt(_effectiveTransportPolicy))
                {
                    if (_effectiveTransportPolicy?["disable_transport_retries"]?.GetValue<bool>() != true ||
                        _effectiveTransportPolicy["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1 ||
                        _effectiveTransportPolicy["effective_retry_policy"]?["MaxUncertainRetries"]?.GetValue<int>() != 0)
                        throw new InvalidOperationException("Runtime inference requires one attempt without uncertain replay.");
                }
                else
                {
                    if (!DateTimeOffset.TryParseExact(_effectiveTransportPolicy?["deadline_utc"]?.GetValue<string>(), "O",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var deadline) ||
                        (time ?? TimeProvider.System).GetUtcNow() >= deadline)
                        throw new InvalidOperationException("The captured planning deadline cannot cover another HTTP attempt.");
                    var tokenLimit = _effectiveTransportPolicy?["max_total_tokens"]?.GetValue<long>()
                        ?? throw new InvalidOperationException("Captured cumulative token allowance is required.");
                    var reviewTokens = _effectiveTransportPolicy?["review_token_headroom"]?.GetValue<long>()
                        ?? throw new InvalidOperationException("Captured review token headroom is required.");
                    if (tokenLimit is <= 0 or > 15_000_000 || reviewTokens < 0 ||
                        totals["session_token_upper_bound"]!.GetValue<long>() > tokenLimit - reviewTokens)
                        sessionExhausted = true;
                }
            }
            if (totals["cost_upper_bound_eur"]!.GetValue<decimal>() + reviewCostHeadroom > await campaign.CostCeilingAsync(ct) || sessionExhausted)
            { campaign.BudgetExceeded(sessionExhausted); throw new InvalidOperationException("The campaign or session cannot cover another HTTP attempt."); }
        }
        await campaign.SaveAsync(Collection, requestId, record, ct);
    }

    internal async Task<JsonObject> CompleteAsync(JsonObject usage, CancellationToken ct)
    {
        var record = await campaign.LoadAsync(Collection, requestId, ct) ?? throw new InvalidOperationException("HTTP reservation missing.");
        if (record["usage"] is JsonObject saved) return UsageWithReservations(record, saved);
        var attempts = record["transport"]!["Attempts"]!.AsArray();
        if (attempts.LastOrDefault()?["Status"]?.GetValue<int>() is not (>= 200 and < 300)) throw new InvalidOperationException("HTTP completion missing.");
        usage = UsageWithReservations(record, usage);
        record["usage"] = usage.DeepClone();
        await campaign.SaveAsync(Collection, requestId, record, ct);
        return usage;
    }

    internal static JsonObject UsageWithReservations(JsonObject record, JsonObject usage)
    {
        var attempts = record["transport"]!["Attempts"]!.AsArray();
        var unknown = attempts.Count(a => PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(a?["Status"]?.GetValue<int>(), hasVerifiedUsage: true, PlanningBenchmarkMeasurements.RetainsAllHttpFailures(record)));
        usage = usage.DeepClone().AsObject();
        usage["transport_attempts"] = attempts.Count;
        usage["uncertain_attempts"] = unknown;
        usage["reserved_input_tokens"] = unknown * record["input_ceiling"]!.GetValue<long>();
        usage["reserved_output_tokens"] = unknown * record["output_ceiling"]!.GetValue<long>();
        usage["reserved_cost_eur"] = unknown * record["cost_ceiling_eur"]!.GetValue<decimal>();
        usage["benchmark_usage_bounded"] = true;
        return usage;
    }

    internal static async Task<JsonObject> AccountingAsync(BenchmarkCampaign campaign, string? requestId = null, JsonObject? replacement = null, CancellationToken ct = default)
    {
        var prefix = campaign.Id + ":";
        var rows = (await campaign.Records.ListAsync(Collection, "benchmark", BenchmarkCampaign.Author, ct))
            .Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(r => r.Key[prefix.Length..], r => JsonNode.Parse(r.Value)!.AsObject(), StringComparer.Ordinal);
        if (requestId is not null && replacement is not null) rows[requestId] = replacement;
        var legacy = await campaign.Records.GetAsync("planning-evaluation-budgets", "benchmark", campaign.Id, BenchmarkCampaign.Author, ct);
        var snapshot = legacy is null ? null : JsonSerializer.Deserialize(legacy.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        if (snapshot is not null && snapshot.EstimatedCostCurrency != "EUR") throw new InvalidOperationException("The existing campaign ledger must be denominated in EUR.");
        long calls = snapshot?.Calls ?? 0, input = snapshot?.InputTokens ?? 0, output = snapshot?.OutputTokens ?? 0;
        long reservedInput = 0, reservedOutput = 0, uncertain = 0, sessionCalls = 0, sessionTokens = 0;
        decimal cost = snapshot?.EstimatedCost ?? 0, reservedCost = 0;
        var session = requestId?.Split(':')[0] + ":";
        foreach (var (key, row) in rows)
        {
            var attempts = row["transport"]!["Attempts"]!.AsArray();
            calls += attempts.Count;
            if (requestId is not null && key.StartsWith(session, StringComparison.Ordinal)) sessionCalls += attempts.Count;
            var usage = row["usage"];
            var pending = attempts.Count(a => PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(a?["Status"]?.GetValue<int>(), usage is not null, PlanningBenchmarkMeasurements.RetainsAllHttpFailures(row)));
            if (requestId is not null && key.StartsWith(session, StringComparison.Ordinal))
                sessionTokens = checked(sessionTokens + pending * checked(row["input_ceiling"]!.GetValue<long>() + row["output_ceiling"]!.GetValue<long>()) +
                    (usage?["input_tokens"]?.GetValue<long>() ?? 0) + (usage?["output_tokens"]?.GetValue<long>() ?? 0));
            uncertain += pending;
            reservedInput += pending * row["input_ceiling"]!.GetValue<long>();
            reservedOutput += pending * row["output_ceiling"]!.GetValue<long>();
            reservedCost += pending * row["cost_ceiling_eur"]!.GetValue<decimal>();
            if (usage is not null)
            {
                input += usage["input_tokens"]!.GetValue<long>(); output += usage["output_tokens"]!.GetValue<long>();
                cost += usage["benchmark_cost_eur"]!.GetValue<decimal>();
            }
        }
        if (requestId is not null)
        {
            var oldRequests = await campaign.Records.ListAsync("planning-evaluation-requests", "benchmark", BenchmarkCampaign.Author, ct);
            sessionCalls += oldRequests.Count(r => r.Key.StartsWith(prefix + session, StringComparison.Ordinal) && !rows.ContainsKey(r.Key[prefix.Length..]));
        }
        var result = new JsonObject { ["calls"] = calls, ["session_calls"] = sessionCalls, ["known_input_tokens"] = input, ["known_output_tokens"] = output,
            ["known_cost_eur"] = cost, ["reserved_input_tokens"] = reservedInput, ["reserved_output_tokens"] = reservedOutput,
            ["reserved_cost_eur"] = reservedCost, ["cost_upper_bound_eur"] = cost + reservedCost, ["unknown_attempts"] = uncertain };
        // Preserve historical accounting hashes when no new captured policy is being admitted.
        if (replacement is not null && PlanningBenchmarkMeasurements.RetainsAllHttpFailures(replacement))
            result["session_token_upper_bound"] = sessionTokens;
        return result;
    }
}
