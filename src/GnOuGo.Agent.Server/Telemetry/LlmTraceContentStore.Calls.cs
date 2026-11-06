using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Telemetry;

public sealed partial class LlmTraceContentStore
{
    public async Task<LlmCallPage> ReadCallsAsync(string session, bool workflow, LlmCallCursor? after, CancellationToken ct, string? selectedRequest = null)
    {
        var initial = after is null;
        after ??= new();
        if (!Enabled || await OwnedSessionAsync(session, workflow, ct) is not { } state) return new([], after, false);
        if (records is not IKeyVaultRecordReader reader) throw new NotSupportedException("The record store does not support bounded metadata inspection.");
        var prefix = workflow ? "flow" : "agent";
        var keyPrefix = workflow ? session + ":" : session + ":" + session + ":";
        var requests = await reader.QueryMetadataAsync(prefix + "-planning-model-requests-v11", Tenant, keyPrefix, Author, after.Requests, ct: ct);
        var receipts = await reader.QueryMetadataAsync(prefix + "-planning-model-receipts-v11", Tenant, keyPrefix, Author, after.Receipts, ct: ct);
        var http = await reader.QueryMetadataAsync(prefix + "-planning-model-http-v11", Tenant, session + ":", Author, after.Http, ct: ct);
        var events = state.InspectionEvents ?? [];
        // Initial history is paged by journal metadata, not expanded again through every old engine event.
        var eventDelta = initial ? Array.Empty<PlanningInspectionEvent>() : events.Where(e => e.Sequence > after.Event).Take(200).ToArray();
        var nextEvent = initial ? events.LastOrDefault()?.Sequence ?? after.Event : eventDelta.LastOrDefault()?.Sequence ?? after.Event;
        var decisionsByRequest = events.Where(e => e.RequestId is not null).GroupBy(e => e.RequestId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var measurements = (state.RequestMeasurements ?? []).ToDictionary(m => m.RequestId, StringComparer.Ordinal);
        string Id(string key) => workflow ? key : key[(session.Length + 1)..];
        var changed = requests.Records.Select(r => Id(r.Key)).Concat(receipts.Records.Select(r => Id(r.Key))).Concat(http.Records.Select(r => r.Key))
            .Concat(eventDelta.Where(e => e.RequestId is not null).Select(e => e.RequestId!))
            .Concat(selectedRequest is null ? Array.Empty<string>() : [selectedRequest])
            .Concat(state.PendingCall is { } pending && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status) ? [pending.Id] : Array.Empty<string>()).Distinct(StringComparer.Ordinal);
        var result = new List<LlmCallProjection>();
        foreach (var id in changed)
        {
            if (!id.StartsWith(session + ":", StringComparison.Ordinal)) continue;
            var key = workflow ? id : session + ":" + id;
            var metadata = requests.Records.FirstOrDefault(r => r.Key == key) ?? await reader.GetMetadataAsync(prefix + "-planning-model-requests-v11", Tenant, key, Author, ct);
            if (metadata is null) continue;
            var measurement = measurements.GetValueOrDefault(id);
            var receipt = receipts.Records.FirstOrDefault(r => r.Key == key) ?? await reader.GetMetadataAsync(prefix + "-planning-model-receipts-v11", Tenant, key, Author, ct);
            var transport = http.Records.FirstOrDefault(r => r.Key == id) ?? await reader.GetMetadataAsync(prefix + "-planning-model-http-v11", Tenant, id, Author, ct);
            // Small manifests only. Historical bodies are not hydrated for a summary.
            var source = await SourceAsync(prefix + "-planning-model-requests-v11", session, key, false, ct);
            var completed = receipt is null ? null : await SourceAsync(prefix + "-planning-model-receipts-v11", session, key, false, ct);
            var generator = state.Request.Options["generator"];
            var decisions = decisionsByRequest.GetValueOrDefault(id) ?? [];
            string Decision(string phase) => decisions.LastOrDefault(e => e.Phase == phase) is { } e ? LlmInspectionLabels.State(e.State) + " — " + e.Reason : "Unavailable";
            result.Add(new(Tenant, session, workflow, id, key, metadata.CreatedAt,
                source?["provider"]?.ToString() ?? generator?["provider"]?.ToString(), source?["model"]?.ToString() ?? generator?["model"]?.ToString(), measurement?.Phase,
                measurement?.BlockId, completed?["completionStatus"]?.ToString() == "output_limit" ? "Response received — output limit reached; incomplete result" : receipt is not null ? "Response received — acceptance is separate" : measurement?.Result == "financial_refusal_before_dispatch" ? LlmInspectionLabels.FinancialRefusal : state.PendingCall?.Id == id && !PlanningStatus.IsTerminal(state.Status)
                    ? "Waiting for provider" : "uncertain — no receipt", Decision("technical_validation"),
                decisions.LastOrDefault(e => e.Phase is "construction_local_judge" or "construction_global_judge") is { } review ? LlmInspectionLabels.State(review.State) + " — " + review.Reason : "Unavailable",
                measurement?.InputTokens ?? LlmTraceCapture.Usage(completed?["usage"], "input_tokens", "prompt_tokens", "inputTokens"),
                measurement?.OutputTokens ?? LlmTraceCapture.Usage(completed?["usage"], "output_tokens", "completion_tokens", "outputTokens"))
            { EvidenceVersion = string.Join(':', metadata.UpdatedAt.UtcTicks, receipt?.UpdatedAt.UtcTicks, transport?.UpdatedAt.UtcTicks,
                decisions.LastOrDefault()?.Sequence, state.Status, measurement?.Result) });
        }
        return new(result, new(requests.Next, receipts.Next, http.Next, nextEvent), requests.HasMore || receipts.HasMore || http.HasMore
            || (events.LastOrDefault()?.Sequence ?? 0) > nextEvent);
    }
}
