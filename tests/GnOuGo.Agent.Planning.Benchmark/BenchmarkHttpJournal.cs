using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;

/// <summary>Admission and encrypted evidence only. All retry decisions belong to AI.Core.</summary>
internal sealed class BenchmarkHttpJournal(BenchmarkCampaign campaign, string requestId, long inputCeiling, long outputCeiling, decimal costCeiling) : ILLMHttpRetryJournal
{
    internal const string Collection = "planning-evaluation-http-attempts";
    internal async Task PrepareAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is null) await SaveAsync(new(), ct);
    }
    public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
        => (await campaign.LoadAsync(Collection, requestId, ct))?["transport"] is { } state
            ? JsonSerializer.Deserialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState) : null;

    public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
    {
        var existing = await campaign.LoadAsync(Collection, requestId, ct);
        var record = existing?.DeepClone().AsObject() ?? new JsonObject
        {
            ["input_ceiling"] = inputCeiling, ["output_ceiling"] = outputCeiling, ["cost_ceiling_eur"] = costCeiling
        };
        if (inputCeiling <= 0 || outputCeiling <= 0 || costCeiling < 0) throw new InvalidOperationException("Conservative attempt limits are required.");
        record["transport"] = JsonSerializer.SerializeToNode(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        var oldCount = existing?["transport"]?["Attempts"]?.AsArray().Count ?? 0;
        if (state.Attempts.Count > oldCount)
        {
            if (state.Attempts.Count != oldCount + 1 || state.Attempts[^1].Status is not null)
                throw new InvalidOperationException("Each new dispatch must have one reserved identity.");
            var totals = await AccountingAsync(campaign, requestId, record, ct);
            if (totals["cost_upper_bound_eur"]!.GetValue<decimal>() > 50m || totals["session_calls"]!.GetValue<long>() > 8)
            { campaign.BudgetExceeded(); throw new InvalidOperationException("The campaign or session cannot cover another HTTP attempt."); }
        }
        await campaign.SaveAsync(Collection, requestId, record, ct);
    }

    internal async Task<JsonObject> CompleteAsync(JsonObject usage, CancellationToken ct)
    {
        var record = await campaign.LoadAsync(Collection, requestId, ct) ?? throw new InvalidOperationException("HTTP reservation missing.");
        if (record["usage"] is JsonObject saved) return saved.DeepClone().AsObject();
        var attempts = record["transport"]!["Attempts"]!.AsArray();
        if (attempts.LastOrDefault()?["Status"]?.GetValue<int>() is not (>= 200 and < 300)) throw new InvalidOperationException("HTTP completion missing.");
        var unknown = attempts.Count(a => a!["Status"] is null);
        usage = usage.DeepClone().AsObject();
        usage["transport_attempts"] = attempts.Count;
        usage["uncertain_attempts"] = unknown;
        usage["reserved_input_tokens"] = unknown * record["input_ceiling"]!.GetValue<long>();
        usage["reserved_output_tokens"] = unknown * record["output_ceiling"]!.GetValue<long>();
        usage["reserved_cost_eur"] = unknown * record["cost_ceiling_eur"]!.GetValue<decimal>();
        usage["benchmark_usage_bounded"] = true;
        record["usage"] = usage.DeepClone();
        await campaign.SaveAsync(Collection, requestId, record, ct);
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
        long reservedInput = 0, reservedOutput = 0, uncertain = 0, sessionCalls = 0;
        decimal cost = snapshot?.EstimatedCost ?? 0, reservedCost = 0;
        var session = requestId?.Split(':')[0] + ":";
        foreach (var (key, row) in rows)
        {
            var attempts = row["transport"]!["Attempts"]!.AsArray();
            calls += attempts.Count;
            if (requestId is not null && key.StartsWith(session, StringComparison.Ordinal)) sessionCalls += attempts.Count;
            var usage = row["usage"];
            var pending = attempts.Count(a => a!["Status"] is null || usage is null && a["Status"]!.GetValue<int>() is >= 200 and < 300);
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
        return new() { ["calls"] = calls, ["session_calls"] = sessionCalls, ["known_input_tokens"] = input, ["known_output_tokens"] = output,
            ["known_cost_eur"] = cost, ["reserved_input_tokens"] = reservedInput, ["reserved_output_tokens"] = reservedOutput,
            ["reserved_cost_eur"] = reservedCost, ["cost_upper_bound_eur"] = cost + reservedCost, ["unknown_attempts"] = uncertain };
    }
}
