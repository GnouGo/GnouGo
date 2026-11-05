using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Read-only projection of owned encrypted planner journals. Never performs model or tool calls.</summary>
public sealed class PlanningInspectionService(PlanningSessionService planning, LocalOperatorAccess access,
    LlmTraceContentStore content, IKeyVaultRecordStore records)
{
    public async Task<JsonObject> ReadAsync(string id, bool workflow, long after, CancellationToken ct)
    {
        var state = await OwnedAsync(id, workflow, ct);
        var events = (state.InspectionEvents ?? []).Where(e => e.Sequence > after).Take(200).ToArray();
        var history = await content.HistoryAsync(id, workflow, ct);
        var calls = new JsonArray();
        foreach (var call in history)
        {
            var requestId = workflow ? call.Key : call.Key[(id.Length + 1)..];
            var measurement = state.RequestMeasurements?.SingleOrDefault(m => m.RequestId == requestId);
            calls.Add(new JsonObject { ["key"] = call.Key, ["requestId"] = requestId, ["createdAtUtc"] = call.CreatedAt,
                ["provider"] = call.Provider, ["model"] = call.Model, ["status"] = measurement?.Result ??
                    (state.PendingCall?.Id == requestId && state.Status == PlanningStatus.Cancelled ? "cancelled — inspect uncertain usage reservations" :
                    state.PendingCall?.Id == requestId && !PlanningStatus.IsTerminal(state.Status) ? "active — waiting for provider" : call.Status),
                ["phase"] = measurement?.Phase ?? "historical phase unavailable", ["blockId"] = measurement?.BlockId,
                ["stepId"] = measurement?.StepId, ["inputTokens"] = call.InputTokens, ["outputTokens"] = call.OutputTokens,
                ["estimatedInputTokens"] = measurement?.EstimatedInputTokens, ["issuedOutputTokens"] = measurement?.IssuedOutputTokens,
                ["durationMilliseconds"] = measurement?.DurationMilliseconds, ["durationBasis"] = measurement?.DurationBasis });
        }
        return new JsonObject { ["sessionId"] = id, ["events"] = JsonSerializer.SerializeToNode(events, PlanningInspectionJsonContext.Default.PlanningInspectionEventArray),
            ["nextEvent"] = events.LastOrDefault()?.Sequence ?? after, ["hasMoreEvents"] = (state.InspectionEvents?.LastOrDefault()?.Sequence ?? 0) > (events.LastOrDefault()?.Sequence ?? after),
            ["calls"] = calls, ["callCount"] = history.Count };
    }

    public async Task<JsonObject> CallAsync(string id, bool workflow, string key, CancellationToken ct)
    {
        var state = await OwnedAsync(id, workflow, ct);
        var logical = await content.LoadJournalAsync(id, workflow, key, ct) ?? throw new KeyNotFoundException("The session does not own this request.");
        var request = JsonNode.Parse(logical.Input!)!.AsObject();
        var requestId = request["clientRequestId"]!.GetValue<string>();
        var prefix = workflow ? "flow" : "agent";
        var http = await records.GetAsync(prefix + "-planning-model-http-v11", state.Request.TenantId, requestId, EfPlanningSessionStore.Author, ct);
        JsonNode? transportRecord = null;
        if (http is not null)
        {
            transportRecord = JsonNode.Parse(http.Value)!;
            var retained = await records.GetAsync(prefix + "-planning-model-requests-v11", state.Request.TenantId, key, EfPlanningSessionStore.Author, ct)
                ?? throw new KeyNotFoundException("The exact retained request is unavailable.");
            var exact = JsonSerializer.Deserialize(retained.Value, PlanningJsonContext.Default.LLMRequest)!;
            var hash = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(exact, PlanningJsonContext.Default.LLMRequest));
            if (transportRecord["tenant"]?.ToString() != state.Request.TenantId || transportRecord["session"]?.ToString() != id ||
                transportRecord["request_hash"]?.ToString() != hash || exact.ClientRequestId != requestId)
                throw new KeyNotFoundException("The HTTP evidence does not belong to this exact request.");
        }
        var budget = await records.GetAsync(prefix + "-planning-budgets-v11", state.Request.TenantId, id, EfPlanningSessionStore.Author, ct);
        var accounting = budget is null ? state.Usage : JsonSerializer.Deserialize(budget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var attempts = accounting?.JournaledAttempts?.Where(pair => pair.Key.StartsWith(requestId + ":attempt:", StringComparison.Ordinal)).Select(pair => pair.Value).ToArray() ?? [];
        var measured = attempts.Where(attempt => attempt.VerifiedUsage is not null).Select(attempt => attempt.VerifiedUsage!).ToArray();
        var raw = new JsonObject { ["sessionId"] = id, ["requestId"] = requestId,
            ["logicalRequest"] = request, ["logicalResponse"] = logical.Output is null ? null : JsonNode.Parse(logical.Output),
            ["responseStatus"] = logical.OutputStatus, ["http"] = transportRecord,
            ["measurement"] = state.RequestMeasurements?.SingleOrDefault(m => m.RequestId == requestId) is { } measurement
                ? JsonSerializer.SerializeToNode(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement) : null,
            ["warnings"] = JsonSerializer.SerializeToNode((state.ThresholdWarnings ?? []).Where(w => w.RequestId == requestId).ToList(), PlanningInspectionJsonContext.Default.ListPlanningThresholdWarning),
            ["accounting"] = budget is null ? null : JsonNode.Parse(budget.Value),
            ["costBreakdown"] = new JsonObject { ["currency"] = accounting?.EstimatedCostCurrency,
                ["measuredUsageTariffEstimate"] = measured.Length == 0 || measured.Any(usage => usage.CostUnavailableReason is not null) ? null : JsonValue.Create(measured.Sum(usage => usage.EstimatedCost)),
                ["uncertainReservation"] = attempts.Length == 0 || attempts.Any(attempt => attempt.VerifiedUsage is null && attempt.CostUnavailableReason is not null) ? null : JsonValue.Create(attempts.Where(attempt => attempt.VerifiedUsage is null).Sum(attempt => attempt.ReservedEstimatedCost)),
                ["unavailableReason"] = accounting?.CostUnavailableReason,
                ["providerInvoiceCost"] = null,
                ["providerReportedCost"] = logical.Output is null ? null : JsonNode.Parse(logical.Output)?["usage"]?["cost"]?.DeepClone(),
                ["providerReportedCurrency"] = logical.Output is null ? null : JsonNode.Parse(logical.Output)?["usage"]?["currency"]?.DeepClone(),
                ["unknownAttemptCount"] = attempts.Count(attempt => attempt.VerifiedUsage is null) },
            ["decisions"] = JsonSerializer.SerializeToNode((state.InspectionEvents ?? []).Where(e => e.RequestId == requestId).ToArray(), PlanningInspectionJsonContext.Default.PlanningInspectionEventArray),
            ["costBasis"] = "Accounting contains conservative estimates and retained uncertain reservations. Provider usage is separate; reasoning tokens are included in output. A provider invoice cost is unavailable unless explicitly supplied.",
            ["effectivePolicy"] = PlanningEndpoints.ToDto(state).EffectivePolicy };
        return JsonNode.Parse(content.Redact(raw.ToJsonString()))!.AsObject();
    }

    private async Task<PlanningSession> OwnedAsync(string id, bool workflow, CancellationToken ct)
    {
        if (!await access.CanInspectAsync()) throw new UnauthorizedAccessException("Local operator access is required.");
        return (await planning.InspectAsync(id, workflow, ct))?.RequireSession() ?? throw new KeyNotFoundException("Planning session not found.");
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PlanningInspectionEvent[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<PlanningThresholdWarning>))]
internal partial class PlanningInspectionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
