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
    public async Task<JsonObject> ReadAsync(string id, bool workflow, long after, CancellationToken ct, LlmCallCursor? callsAfter = null)
    {
        var state = await OwnedAsync(id, workflow, ct);
        var events = (state.InspectionEvents ?? []).Where(e => e.Sequence > after).Take(200).ToArray();
        var history = await content.ReadCallsAsync(id, workflow, callsAfter, ct);
        var calls = new JsonArray();
        foreach (var call in history.Calls)
        {
            var requestId = call.RequestId;
            var measurement = state.RequestMeasurements?.SingleOrDefault(m => m.RequestId == requestId);
            calls.Add(new JsonObject { ["key"] = call.JournalKey, ["requestId"] = requestId, ["createdAtUtc"] = call.CreatedAt,
                ["provider"] = call.Provider, ["model"] = call.Model, ["source"] = call.Source,
                ["responseStatus"] = call.ResponseStatus, ["technicalValidation"] = call.TechnicalValidation, ["businessReview"] = call.BusinessReview,
                ["status"] = measurement?.Result ??
                    (state.PendingCall?.Id == requestId && state.Status == PlanningStatus.Cancelled ? "cancelled — inspect uncertain usage reservations" :
                    state.PendingCall?.Id == requestId && !PlanningStatus.IsTerminal(state.Status) ? "active — waiting for provider" : call.ResponseStatus),
                ["phase"] = measurement?.Phase ?? "historical phase unavailable", ["blockId"] = measurement?.BlockId,
                ["stepId"] = measurement?.StepId, ["inputTokens"] = call.InputTokens, ["outputTokens"] = call.OutputTokens,
                ["estimatedInputTokens"] = measurement?.EstimatedInputTokens, ["issuedOutputTokens"] = measurement?.IssuedOutputTokens,
                ["durationMilliseconds"] = measurement?.DurationMilliseconds, ["durationBasis"] = measurement?.DurationBasis });
        }
        return new JsonObject { ["sessionId"] = id, ["events"] = JsonSerializer.SerializeToNode(events, PlanningInspectionJsonContext.Default.PlanningInspectionEventArray),
            ["nextEvent"] = events.LastOrDefault()?.Sequence ?? after, ["hasMoreEvents"] = (state.InspectionEvents?.LastOrDefault()?.Sequence ?? 0) > (events.LastOrDefault()?.Sequence ?? after),
            ["calls"] = calls, ["callCount"] = history.Calls.Count,
            ["nextCallCursor"] = JsonSerializer.SerializeToNode(history.Cursor, PlanningInspectionJsonContext.Default.LlmCallCursor), ["hasMoreCalls"] = history.HasMore };
    }

    public async Task<JsonObject> CallAsync(string id, bool workflow, string key, CancellationToken ct)
    {
        var state = await OwnedAsync(id, workflow, ct);
        var detail = await content.JournalDocumentsAsync(id, workflow, key, ct);
        var requestId = detail["requestId"]!.ToString();
        var prefix = workflow ? "flow" : "agent";
        var budget = await records.GetAsync(prefix + "-planning-budgets-v11", state.Request.TenantId, id, EfPlanningSessionStore.Author, ct);
        var accounting = budget is null ? state.Usage : JsonSerializer.Deserialize(budget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var historicalMissing = accounting is null || !accounting.CostEstimateAvailable && accounting.CostUnavailableReason == "historical_cost_evidence_unavailable";
        var attempts = accounting?.JournaledAttempts?.Where(pair => pair.Key.StartsWith(requestId + ":attempt:", StringComparison.Ordinal)).ToArray() ?? [];
        var measured = attempts.Where(pair => pair.Value.VerifiedUsage is not null).Select(pair => pair.Value.VerifiedUsage!).ToArray();
        var retainedAttempts = budget is null ? null : JsonNode.Parse(budget.Value)?["journaledAttempts"];
        detail["accounting"] = budget is null && historicalMissing ? null : new JsonObject(attempts.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key,
            budget is not null ? retainedAttempts?[pair.Key]?.DeepClone() : JsonSerializer.SerializeToNode(pair.Value, PlanningJsonContext.Default.LLMJournaledAttempt))));
        detail["measurement"] = state.RequestMeasurements?.SingleOrDefault(m => m.RequestId == requestId) is { } measurement
            ? JsonSerializer.SerializeToNode(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement) : null;
        detail["warnings"] = JsonSerializer.SerializeToNode((state.ThresholdWarnings ?? []).Where(w => w.RequestId == requestId).ToList(), PlanningInspectionJsonContext.Default.ListPlanningThresholdWarning);
        detail["decisions"] = JsonSerializer.SerializeToNode((state.InspectionEvents ?? []).Where(e => e.RequestId == requestId).ToArray(), PlanningInspectionJsonContext.Default.PlanningInspectionEventArray);
        detail["costBreakdown"] = new JsonObject { ["currency"] = historicalMissing ? null : accounting?.EstimatedCostCurrency,
            ["measuredUsageTariffEstimate"] = historicalMissing || measured.Length == 0 || measured.Any(u => u.CostUnavailableReason is not null) ? null : JsonValue.Create(measured.Sum(u => u.EstimatedCost)),
            ["uncertainReservation"] = historicalMissing || attempts.Length == 0 || attempts.Any(p => p.Value.VerifiedUsage is null && p.Value.CostUnavailableReason is not null) ? null : JsonValue.Create(attempts.Where(p => p.Value.VerifiedUsage is null).Sum(p => p.Value.ReservedEstimatedCost)),
            ["providerReportedCost"] = detail["usage"]?["cost"]?.DeepClone(), ["providerReportedCurrency"] = detail["usage"]?["currency"]?.DeepClone(),
            ["unavailableReason"] = accounting?.CostUnavailableReason, ["unknownAttemptCount"] = attempts.Count(p => p.Value.VerifiedUsage is null) };
        detail["costBasis"] = "Measured usage × captured tariff is an estimate. Provider-reported cost and uncertain reservations are separate. Reasoning tokens are included in output totals. Missing historical values are unavailable.";
        return JsonNode.Parse(content.Redact(detail.ToJsonString()))!.AsObject();
    }

    public async Task<GnOuGo.KeyVault.Core.Services.KeyVaultRecordRange> PageAsync(string id, bool workflow, string key, string document, string? version, int offset, int count, CancellationToken ct)
        => await content.ReadDocumentAsync(await OwnedDocumentsAsync(id, workflow, key, ct), document, version, offset, count, ct);

    public async Task<LlmDocumentSearch> SearchAsync(string id, bool workflow, string key, string document, string version, string query, int offset, CancellationToken ct)
        => await content.SearchDocumentAsync(await OwnedDocumentsAsync(id, workflow, key, ct), document, version, query, offset, ct);

    private async Task<JsonObject> OwnedDocumentsAsync(string id, bool workflow, string key, CancellationToken ct)
    {
        await OwnedAsync(id, workflow, ct);
        return await content.JournalDocumentsAsync(id, workflow, key, ct);
    }

    public async Task ExportAsync(string id, bool workflow, string key, Stream stream, CancellationToken ct)
    {
        var detail = await CallAsync(id, workflow, key, ct);
        await content.ExportDocumentsAsync(detail, stream, ct);
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
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LlmCallCursor))]
internal partial class PlanningInspectionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
