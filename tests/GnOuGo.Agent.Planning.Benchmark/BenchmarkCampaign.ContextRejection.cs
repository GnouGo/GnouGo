using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Core.Planning;

internal sealed partial class BenchmarkCampaign
{
    // A complete terminal rejection closes only this tool-free inference identity.
    // Every physical reservation remains in the original HTTP accounting.
    internal async Task RetainContextRejectionAsync(string requestId, CancellationToken ct)
    {
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct);
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct);
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct);
        if (request is null || request["clientRequestId"]?.ToString() != requestId ||
            request["tools"] is not (null or JsonArray { Count: 0 }) || request["useBackgroundMode"]?.GetValue<bool>() != false ||
            request["maxTokens"]?.GetValue<int>() is not > 0 ||
            failure?["stage"]?.ToString() != "dispatch" || failure["kind"]?.ToString() != "InvalidRequest" ||
            failure["status_code"]?.GetValue<int>() != 400 || failure["retryable"]?.GetValue<bool>() != false ||
            failure["attempt_count"]?.GetValue<int>() != 1 || failure["retry_exhausted"]?.GetValue<bool>() != false ||
            journal?["transport"]?["Attempts"] is not JsonArray { Count: 1 } attempts ||
            attempts[0]?["Id"]?.ToString() is not { Length: > 0 } attemptId || !attemptId.StartsWith(requestId + ":", StringComparison.Ordinal) ||
            attempts[0]?["Status"]?.GetValue<int>() != 400 || attempts[0]?["Failure"] is not null ||
            attempts[0]?["ProtocolComplete"]?.GetValue<bool>() == true || attempts[0]?["UsageKnown"]?.GetValue<bool>() == true ||
            attempts[0]?["AssembledBody"] is not null || journal["usage"] is not null ||
            await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null ||
            attempts[0]?["Body"]?.ToString() is not { Length: > 0 and <= 65536 } body)
            throw new InvalidOperationException("An exact complete tool-free context rejection is required; no reservation can be released.");
        JsonNode response;
        try { response = JsonNode.Parse(body)!; }
        catch (System.Text.Json.JsonException) { throw new InvalidOperationException("The retained context rejection is not complete JSON."); }
        if (response is not JsonObject { Count: 1 } || response["error"] is not JsonObject error ||
            error["code"]?.ToString() != "context_length_exceeded" || error["type"]?.ToString() != "invalid_request_error" ||
            error["param"]?.ToString() != "messages" || error["message"]?.ToString() is not { Length: > 0 })
            throw new InvalidOperationException("The response does not prove the exact terminal input-context rejection.");
        static string Fingerprint(JsonNode value) => PlanningGraphCompiler.Fingerprint(value.ToJsonString());
        var evidence = new JsonObject { ["request_id"] = requestId, ["request_hash"] = Fingerprint(request),
            ["http_journal_hash"] = Fingerprint(journal), ["failure_hash"] = Fingerprint(failure),
            ["verification"] = "complete_http_400_context_length_exceeded_messages", ["reservation_retained"] = true,
            ["request_replay_forbidden"] = true, ["workflow_execution_replay_forbidden"] = true };
        if (await LoadAsync("planning-evaluation-closures", requestId, ct) is { } retained)
        {
            if (retained["reason"]?.ToString() != "provider_context_rejected" || !JsonNode.DeepEquals(retained["evidence"], evidence))
                throw new InvalidOperationException("The retained rejection is immutable or its original evidence changed.");
            return;
        }
        await SaveAsync("planning-evaluation-closures", requestId, new() { ["reason"] = "provider_context_rejected",
            ["outcome"] = "failed", ["evidence"] = evidence, ["retained_at"] = DateTimeOffset.UtcNow.ToString("O") }, ct);
        StopReason = null;
    }
}
