using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

internal sealed partial class BenchmarkCampaign
{
    private const string PermanentRetentionReason = "operator_retained_uncertain_planning";
    internal const string SingleHttp502Retention = "single-http-502";

    // Called before any continuation, approval, execution or new request in the abandoned session.
    // Historical closures retain their original request-only behavior.
    internal async Task RequireRunNotRetainedAsync(string sessionId, CancellationToken ct = default)
    {
        foreach (var row in (await Records.ListAsync("planning-evaluation-closures", "benchmark", Author, ct))
            .Where(r => r.Key.StartsWith(Id + ":", StringComparison.Ordinal)))
        {
            var closure = JsonNode.Parse(row.Value)!.AsObject();
            if (closure["reason"]?.ToString() != PermanentRetentionReason) continue;
            ValidatePermanentClosure(closure, row.Key[(Id.Length + 1)..]);
            var abandoned = closure["evidence"]!["session_id"]!.ToString();
            if (sessionId == abandoned || sessionId == abandoned + "-execution" || sessionId == abandoned + "-copilot")
                throw new InvalidOperationException("This planning run is permanently retained as inconclusive and cannot be continued or executed.");
        }
    }

    internal async Task<JsonObject> InspectPermanentRetentionAsync(string collection, string runKey, string requestId, CancellationToken ct,
        string? retentionKind = null)
    {
        ValidateRetentionKind(retentionKind);
        if (collection is not ("planning-evaluation-runs" or SchemaPortabilityCampaign.Collection))
            throw new ArgumentException("Choose an existing benchmark planning-run collection.");
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (await LoadAsync("planning-evaluation-closures", requestId, ct) is { } retained)
        {
            ValidatePermanentClosure(retained, requestId);
            if (retained["evidence"]?["run_collection"]?.ToString() != collection ||
                retained["evidence"]?["run_key"]?.ToString() != runKey ||
                retained["evidence"]?["retention_kind"]?.ToString() != retentionKind)
                throw new InvalidOperationException("The existing closure belongs to a different run or retention kind.");
            var inspection = retained.DeepClone().AsObject();
            inspection["already_retained"] = true;
            inspection["existing_closure_hash"] = Hash(retained);
            return inspection;
        }
        var run = await LoadAsync(collection, runKey, ct) ?? throw new InvalidOperationException("No retained planning run.");
        var state = run["session"]?.Deserialize(PlanningJsonContext.Default.PlanningSession)
            ?? throw new InvalidOperationException("No retained planning session.");
        var pending = state.PendingCall;
        if (state.SchemaVersion != 10 || state.Request.TenantId != "benchmark" ||
            state.Status != PlanningStatus.Stopped || state.RequiresPlanningRevision ||
            state.Construction?.Version is not (1 or 2) || pending is null || pending.Id != requestId ||
            pending.Purpose is not ("construction_intent" or "construction_select" or "construction_discovery" or
                "construction_draft" or "construction_local_judge" or "construction_intermediate_judge" or "replan") ||
            pending.Request.ClientRequestId != requestId || !requestId.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal) ||
            pending.Request.Tools is { Count: > 0 } || pending.Request.MaxTokens is not > 0 ||
            !state.Diagnostics.Any(d => d.Code == "MODEL_DISPATCH_UNVERIFIABLE") ||
            state.Yaml is not null || state.ApprovedHash is not null || state.ApprovalReview is not null || state.SavedAgentId is not null ||
            run["artifact_hash"] is not null || run["execution_id"] is not null || run["execution_run_id"] is not null ||
            run["execution_started"] is not null || run["execution"] is not null ||
            run["result"] is not JsonObject result || result["execution_correct"]?.GetValue<bool>() == true ||
            result["execution_oracle"]?.GetValue<bool>() == true ||
            result["execution_variants"] is JsonArray { Count: > 0 } ||
            collection == SchemaPortabilityCampaign.Collection &&
                (runKey != "run:" + state.Request.SessionId || result["status"]?.ToString() != "stopped" ||
                 result["execution_status"]?.ToString() != "not_started"))
            throw new InvalidOperationException("Permanent retention requires the exact stopped, unexecuted, tool-free uncertain planning request.");
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct);
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct);
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct);
        var policy = await LoadAsync("planning-evaluation-session-policy", state.Request.SessionId, ct);
        var retry = await LoadAsync("planning-evaluation-configuration", "http-retry-policy", ct);
        if (request is null || !JsonNode.DeepEquals(request, JsonSerializer.SerializeToNode(pending.Request, PlanningJsonContext.Default.LLMRequest)) ||
            policy?["construction_review_headroom"]?.GetValue<int>() is not > 0 || retry is null ||
            policy["max_physical_attempts"]?.GetValue<int>() != state.Request.MaxModelCalls ||
            policy["max_input_tokens"]?.GetValue<int>() != state.Request.Generation.MaxInputTokensPerRequest ||
            policy["max_output_tokens"]?.GetValue<int>() != state.Request.Generation.MaxOutputTokens ||
            policy["max_corrections"]?.GetValue<int>() != state.Request.MaxReplanAttempts ||
            state.Construction.Version == 2 && policy["construction_version"]?.GetValue<int>() != 2 ||
            !JsonNode.DeepEquals(policy["construction_limits"], state.Request.Options["construction_limits"]) ||
            !JsonNode.DeepEquals(policy["construction_input_limits"], state.Request.Options["construction_input_limits"]) ||
            failure?["stage"]?.ToString() != "dispatch" || failure["retry_exhausted"]?.GetValue<bool>() != true ||
            failure["retryable"]?.GetValue<bool>() != true ||
            journal?["transport"]?["Attempts"] is not JsonArray { Count: 1 } attempts ||
            attempts[0]?["Id"]?.ToString() is not { Length: > 0 } ||
            !MatchesPermanentFailure(failure, attempts[0], retentionKind) ||
            failure["attempt_count"]?.GetValue<int>() != attempts.Count ||
            journal["input_ceiling"]?.GetValue<long>() is not { } inputCeiling ||
            inputCeiling < (JsonSerializer.SerializeToUtf8Bytes(pending.Request, PlanningJsonContext.Default.LLMRequest).LongLength + 2) / 3 + 256 ||
            journal["output_ceiling"]?.GetValue<long>() is not { } outputCeiling || outputCeiling < pending.Request.MaxTokens ||
            journal["cost_ceiling_eur"]?.GetValue<decimal>() is not >= 0 ||
            !ValidRetainedQuote(journal["exchange_quote"] as JsonObject) ||
            journal["usage"] is not null ||
            await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null)
            throw new InvalidOperationException("Permanent retention requires matching unknown transport evidence and a complete unchanged reservation.");
        var source = (run["source"] ?? result["source_commit"])?.ToString();
        if (source is null || source.Length != 40 || !source.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The original source identity is required.");
        if (run["source"] is not null && result["source_commit"] is not null && run["source"]!.ToString() != result["source_commit"]!.ToString() ||
            run["manifest"] is JsonObject manifest &&
                (manifest["production_sha"]?.ToString() != source || manifest["harness_sha"]?.ToString() != source))
            throw new InvalidOperationException("The retained source and run manifest disagree.");
        if (journal["effective_transport_policy"] is JsonObject effective &&
            (journal["effective_transport_policy_hash"]?.ToString() != Hash(effective) ||
             effective["version"]?.GetValue<int>() != 1 || effective["disable_transport_retries"]?.GetValue<bool>() != true ||
             effective["allow_http_recovery"]?.GetValue<bool>() != false ||
             effective["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1))
            throw new InvalidOperationException("The issued transport policy permits recovery; this terminal operator cannot reinterpret it.");
        if (journal["effective_transport_policy"] is null && journal["effective_transport_policy_hash"] is not null ||
            retentionKind == SingleHttp502Retention && journal["effective_transport_policy"] is not JsonObject)
            throw new InvalidOperationException("The captured effective transport policy is missing.");
        var evidence = new JsonObject
        {
            ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = collection,
            ["run_key"] = runKey, ["session_id"] = state.Request.SessionId, ["revision"] = state.Revision,
            ["request_id"] = requestId, ["phase"] = pending.Purpose, ["source"] = source,
            ["run_hash"] = Hash(run), ["request_hash"] = Hash(request), ["http_journal_hash"] = Hash(journal),
            ["failure_hash"] = Hash(failure), ["session_policy_hash"] = Hash(policy),
            ["campaign_retry_policy_hash"] = Hash(retry),
            ["financial_admission_hash"] = Hash(new JsonObject { ["input_ceiling"] = inputCeiling, ["output_ceiling"] = outputCeiling,
                ["cost_ceiling_eur"] = journal["cost_ceiling_eur"]!.DeepClone(), ["exchange_quote"] = journal["exchange_quote"]!.DeepClone() }),
            ["effective_transport_policy_hash"] = journal["effective_transport_policy"] is JsonObject transport ? Hash(transport) : null,
            ["transport_policy_evidence"] = journal["effective_transport_policy"] is null
                ? "retained_incremental_session_policy_and_original_source" : "captured_effective_request_policy",
            ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, requestId, ct: ct)
        };
        if (retentionKind is not null) evidence["retention_kind"] = retentionKind;
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    // The caller holds the campaign lease. Inspect is read-only; apply publishes exactly one
    // immutable closure. Neither path constructs a provider, alters evidence or settles usage.
    internal async Task<JsonObject> ApplyPermanentRetentionAsync(string collection, string runKey, string requestId,
        string expectedEvidenceHash, string operatorIdentity, string authorization, CancellationToken ct, string? retentionKind = null)
    {
        ValidateRetentionKind(retentionKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorization);
        if (operatorIdentity.Length > 256 || authorization.Length > 4096 ||
            expectedEvidenceHash.Length != 64 || !expectedEvidenceHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Bounded operator authorization and an exact evidence hash are required.");
        var authorizationHash = Hash(new JsonObject { ["operator"] = operatorIdentity, ["authorization"] = authorization });
        if (await LoadAsync("planning-evaluation-closures", requestId, ct) is { } existing)
        {
            ValidatePermanentClosure(existing, requestId);
            if (existing["reason"]?.ToString() != PermanentRetentionReason ||
                existing["evidence_hash"]?.ToString() != expectedEvidenceHash ||
                existing["authorization_hash"]?.ToString() != authorizationHash ||
                existing["evidence"]?["run_collection"]?.ToString() != collection ||
                existing["evidence"]?["run_key"]?.ToString() != runKey ||
                existing["evidence"]?["retention_kind"]?.ToString() != retentionKind)
                throw new InvalidOperationException("The existing closure is immutable and does not match this authorization.");
            return existing;
        }
        var inspected = await InspectPermanentRetentionAsync(collection, runKey, requestId, ct, retentionKind);
        if (inspected["evidence_hash"]?.ToString() != expectedEvidenceHash)
            throw new InvalidOperationException("Retention evidence changed; inspect and review the exact current evidence.");
        var closure = inspected.DeepClone().AsObject();
        closure["reason"] = PermanentRetentionReason;
        closure["operator"] = operatorIdentity; closure["authorization"] = authorization;
        closure["authorization_hash"] = authorizationHash;
        closure["retained_at"] = DateTimeOffset.UtcNow.ToString("O");
        await SaveAsync("planning-evaluation-closures", requestId, closure, ct);
        return closure;
    }

    private void ValidatePermanentClosure(JsonObject closure, string requestId)
    {
        if (closure["reason"]?.ToString() != PermanentRetentionReason ||
            closure["evidence"] is not JsonObject evidence || evidence["version"]?.GetValue<int>() != 1 ||
            evidence["retention_kind"]?.ToString() is not (null or SingleHttp502Retention) ||
            evidence["campaign"]?.ToString() != Id || evidence["tenant"]?.ToString() != "benchmark" ||
            evidence["request_id"]?.ToString() != requestId ||
            evidence["session_id"]?.ToString() is not { Length: > 0 } sessionId ||
            !requestId.StartsWith(sessionId + ":", StringComparison.Ordinal) ||
            closure["evidence_hash"]?.ToString() != Hash(evidence) ||
            closure["authorization_hash"]?.ToString() != Hash(new JsonObject
                { ["operator"] = closure["operator"]?.DeepClone(), ["authorization"] = closure["authorization"]?.DeepClone() }) ||
            string.IsNullOrWhiteSpace(closure["operator"]?.ToString()) || string.IsNullOrWhiteSpace(closure["authorization"]?.ToString()) ||
            closure["outcome"]?.ToString() != "inconclusive" ||
            closure["reservation_retained"]?.GetValue<bool>() != true ||
            closure["request_replay_forbidden"]?.GetValue<bool>() != true ||
            closure["run_continuation_forbidden"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The permanent retention record has invalid identity or evidence bindings.");
    }

    private static void ValidateRetentionKind(string? retentionKind)
    {
        if (retentionKind is not (null or SingleHttp502Retention))
            throw new ArgumentException("The only explicit HTTP retention kind is single-http-502.", nameof(retentionKind));
    }

    private static bool MatchesPermanentFailure(JsonObject failure, JsonNode? attempt, string? retentionKind)
        => retentionKind == SingleHttp502Retention
            ? failure["kind"]?.ToString() == "ServiceUnavailable" && failure["status_code"]?.GetValue<int>() == 502 &&
              attempt?["Status"]?.GetValue<int>() == 502 && attempt["Failure"] is null &&
              attempt["Body"] is JsonValue body && body.TryGetValue<string>(out var text) && IsRetainedErrorOnlyBody(text)
            : failure["kind"]?.ToString() is "Timeout" or "Transport" && failure["status_code"] is null &&
              attempt?["Status"] is null && attempt?["Body"] is null && attempt?["Failure"]?.ToString() is "timeout" or "transport";

    private static bool IsRetainedErrorOnlyBody(string? body)
    {
        // A status code cannot exclude a model response or settle its usage.
        // Recognize only a bounded error envelope; unfamiliar bodies stay blocked.
        if (string.IsNullOrWhiteSpace(body) || body.Length > 65_536) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ChatResponseParser.ExtractUsage(root) is not null) return false;
            if (!root.TryGetProperty("error", out var error))
            {
                var fields = root.EnumerateObject().ToArray();
                return fields.Length == 3 && fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == 3 &&
                    fields.All(p => p.Name is "errorCode" or "errorMessage" or "errorDetail" &&
                        p.Value.ValueKind == JsonValueKind.String) &&
                    !string.IsNullOrWhiteSpace(root.GetProperty("errorCode").GetString()) &&
                    !string.IsNullOrWhiteSpace(root.GetProperty("errorMessage").GetString());
            }
            if (root.EnumerateObject().Count() != 1 || error.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in error.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("message" or "type" or "code" or "param") ||
                    property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            return error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(message.GetString());
        }
        catch (JsonException) { return false; }
    }

    private static string Hash(JsonObject value) => PlanningGraphCompiler.Fingerprint(value.ToJsonString());

    private static bool ValidRetainedQuote(JsonObject? quote)
    {
        try
        {
            return quote?["source_currency"]?.ToString() is { Length: 3 } currency && currency.All(c => c is >= 'A' and <= 'Z') &&
                quote["target_currency"]?.ToString() == "EUR" && quote["rate"]?.GetValue<decimal>() > 0 &&
                !string.IsNullOrWhiteSpace(quote["source"]?.ToString()) &&
                quote["as_of_utc"]?.GetValue<DateTimeOffset>() is { } asOf && asOf != default && asOf <= DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }
}
