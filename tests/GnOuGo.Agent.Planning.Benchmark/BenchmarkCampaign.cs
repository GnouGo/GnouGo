using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;

/// <summary>Evaluation evidence uses the existing public encrypted record boundary, scoped to one campaign.</summary>
internal sealed class BenchmarkCampaign(IKeyVaultRecordStore records, string id)
{
    internal const string Author = "GnOuGo.Planning.Benchmark";
    internal string Id { get; } = ValidateId(id);
    internal IKeyVaultRecordStore Records => records;
    internal string? StopReason { get; private set; }
    private static string ValidateId(string value) => value.Length is >= 1 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        ? value : throw new ArgumentException("Invalid campaign identifier.");
    internal async Task<JsonObject?> LoadAsync(string collection, string key, CancellationToken ct = default)
        => await records.GetAsync(collection, "benchmark", Id + ":" + key, Author, ct) is { } record ? JsonNode.Parse(record.Value)!.AsObject() : null;
    internal Task SaveAsync(string collection, string key, JsonObject value, CancellationToken ct = default)
        => records.UpsertAsync(collection, "benchmark", Id + ":" + key, value.ToJsonString(), Author, ct);
    internal async Task PinAsync(JsonObject configuration, CancellationToken ct)
    {
        var saved = await LoadAsync("planning-evaluation-configuration", "configuration", ct);
        if (saved is not null && !JsonNode.DeepEquals(saved, configuration)) throw new InvalidOperationException("The campaign configuration changed; no model request was dispatched.");
        if (saved is null) await SaveAsync("planning-evaluation-configuration", "configuration", configuration, ct);
    }
    internal async Task<bool> HasUncertainRequestAsync(CancellationToken ct)
    {
        foreach (var request in (await records.ListAsync("planning-evaluation-requests", "benchmark", Author, ct)).Where(r => r.Key.StartsWith(Id + ":", StringComparison.Ordinal)))
            if (await records.GetAsync("planning-evaluation-receipts", "benchmark", request.Key, Author, ct) is null) return true;
        return false;
    }
    internal async Task<LLMResponse> CallAsync(LLMRequest request, Func<CancellationToken, Task> preflight, Func<CancellationToken, Task<LLMResponse>> dispatch, CancellationToken ct)
    {
        var key = request.ClientRequestId ?? throw new InvalidOperationException("A reserved request identity is required.");
        if (await LoadAsync("planning-evaluation-receipts", key, ct) is { } receipt)
            return JsonSerializer.Deserialize(receipt, PlanningJsonContext.Default.LLMResponse)!;
        if (await HasUncertainRequestAsync(ct))
        { StopReason = "uncertain_dispatch"; throw new InvalidOperationException("The campaign has an uncertain dispatch; no request was resent."); }
        await preflight(ct);
        await SaveAsync("planning-evaluation-requests", key, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), ct);
        try
        {
            var response = await dispatch(ct);
            // Preserve evidence even when cancellation arrives after completion.
            await SaveAsync("planning-evaluation-receipts", key, JsonSerializer.SerializeToNode(response, PlanningJsonContext.Default.LLMResponse)!.AsObject(), CancellationToken.None);
            return response;
        }
        catch { StopReason = "uncertain_dispatch"; throw; }
    }
    internal void BudgetExceeded() => StopReason = "campaign_budget";
}
