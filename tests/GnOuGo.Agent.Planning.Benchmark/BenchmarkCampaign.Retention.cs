using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

internal sealed partial class BenchmarkCampaign
{
    private const string PermanentRetentionReason = "operator_retained_uncertain_planning";
    internal const string SingleHttp500Retention = "single-http-500";
    internal const string SingleHttp502Retention = "single-http-502";
    internal const string SingleHttp504Retention = "single-http-504";
    internal const string InterruptedProcessRetention = "interrupted-process";
    internal const string ExhaustedPlanningRetriesRetention = "exhausted-planning-retries";

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
        string? retentionKind = null, BenchmarkFrozenBaselineArm? frozenBaseline = null)
    {
        ValidateRetentionKind(retentionKind);
        var frozenProof = frozenBaseline?.Read();
        if (frozenProof is not null && retentionKind is InterruptedProcessRetention or ExhaustedPlanningRetriesRetention)
            throw new InvalidOperationException("A frozen baseline proof cannot authorize interruption or captured-retry retention.");
        if (collection is not ("planning-evaluation-runs" or SchemaPortabilityCampaign.Collection))
            throw new ArgumentException("Choose an existing benchmark planning-run collection.");
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (await LoadAsync("planning-evaluation-closures", requestId, ct) is { } retained)
        {
            ValidatePermanentClosure(retained, requestId);
            BenchmarkFrozenBaselineArm.RequireSame(retained["evidence"]?["frozen_arm"], frozenProof);
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
        var interrupted = retentionKind == InterruptedProcessRetention;
        var capturedRetries = retentionKind == ExhaustedPlanningRetriesRetention;
        var result = run["result"] as JsonObject;
        if (state.SchemaVersion != 10 || state.Request.TenantId != "benchmark" ||
            state.Status != (interrupted ? PlanningStatus.Generating : PlanningStatus.Stopped) || state.RequiresPlanningRevision ||
            state.Construction?.Version is not (1 or 2) || pending is null || pending.Id != requestId ||
            pending.Purpose is not ("construction_intent" or "construction_select" or "construction_discovery" or
                "construction_draft" or "construction_local_judge" or "construction_intermediate_judge" or "replan") ||
            pending.Request.ClientRequestId != requestId || !requestId.StartsWith(state.Request.SessionId + ":", StringComparison.Ordinal) ||
            pending.Request.Tools is { Count: > 0 } || pending.Request.MaxTokens is not > 0 ||
            !interrupted && !state.Diagnostics.Any(d => d.Code == "MODEL_DISPATCH_UNVERIFIABLE") ||
            state.Yaml is not null || state.ApprovedHash is not null || state.ApprovalReview is not null || state.SavedAgentId is not null ||
            run["artifact_hash"] is not null || run["execution_id"] is not null || run["execution_run_id"] is not null ||
            run["execution_started"] is not null || run["execution"] is not null ||
            (interrupted
                ? collection != "planning-evaluation-runs" || run["result"] is not null ||
                  run["final_review"]?.GetValue<bool>() == true ||
                  state.Phase != (pending.Purpose == "replan" ? PlanningPhase.Replanning : pending.Purpose) ||
                  run["response_measurements"]?[requestId] is not null || run["usage_receipts"]?[requestId] is not null ||
                  run["first_proposal"]?["request_id"]?.ToString() == requestId
                : result is null || result["execution_correct"]?.GetValue<bool>() == true ||
                  result["execution_oracle"]?.GetValue<bool>() == true ||
                  result["execution_variants"] is JsonArray { Count: > 0 } ||
                  collection == SchemaPortabilityCampaign.Collection &&
                    (runKey != "run:" + state.Request.SessionId || result["status"]?.ToString() != "stopped" ||
                     result["execution_status"]?.ToString() != "not_started")))
            throw new InvalidOperationException("Permanent retention requires the exact unexecuted, tool-free uncertain planning request in its selected terminal or interrupted mode.");
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct);
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct);
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct);
        var policy = await LoadAsync("planning-evaluation-session-policy", state.Request.SessionId, ct);
        var retry = await LoadAsync("planning-evaluation-configuration", "http-retry-policy", ct);
        if (request is null || !JsonNode.DeepEquals(request, JsonSerializer.SerializeToNode(pending.Request, PlanningJsonContext.Default.LLMRequest)) ||
            policy?["construction_review_headroom"]?.GetValue<int>() is not > 0 || retry is null ||
            policy["max_physical_attempts"]?.GetValue<int>() != (capturedRetries
                ? Math.Min(state.Request.MaxModelCalls, PlanningBudgetOptions.Parse(state.Request.Options)?.MaxCalls ?? state.Request.MaxModelCalls)
                : state.Request.MaxModelCalls) ||
            policy["max_input_tokens"]?.GetValue<int>() != state.Request.Generation.MaxInputTokensPerRequest ||
            policy["max_output_tokens"]?.GetValue<int>() != state.Request.Generation.MaxOutputTokens ||
            policy["max_corrections"]?.GetValue<int>() != state.Request.MaxReplanAttempts ||
            state.Construction.Version == 2 && policy["construction_version"]?.GetValue<int>() != 2 ||
            !JsonNode.DeepEquals(policy["construction_limits"], state.Request.Options["construction_limits"]) ||
            !JsonNode.DeepEquals(policy["construction_input_limits"], state.Request.Options["construction_input_limits"]) ||
            journal?["transport"]?["Attempts"] is not JsonArray attempts ||
            (capturedRetries ? attempts.Count is < 1 or > 3 : attempts.Count != 1) ||
            attempts[0]?["Id"]?.ToString() is not { Length: > 0 } ||
            (interrupted
                ? failure is not null || attempts[0]?["Status"] is not null || attempts[0]?["Body"] is not null ||
                  attempts[0]?["Failure"] is not null || attempts[0]?["ContentType"] is not null ||
                  attempts[0]?["RetryAfter"] is not null || attempts[0]?["NotBefore"] is not null ||
                  !attempts[0]!["Id"]!.ToString().StartsWith(requestId + ":", StringComparison.Ordinal) ||
                  journal["transport"]?["Fingerprint"]?.ToString() is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit)
                : capturedRetries ? !MatchesCapturedPlanningFailure(state, policy, journal, attempts, failure)
                : failure?["stage"]?.ToString() != "dispatch" || failure["retry_exhausted"]?.GetValue<bool>() != true ||
                  failure["retryable"]?.GetValue<bool>() != true || failure["attempt_count"]?.GetValue<int>() != attempts.Count ||
                  !MatchesPermanentFailure(failure, attempts[0], retentionKind)) ||
            journal["input_ceiling"]?.GetValue<long>() is not { } inputCeiling ||
            inputCeiling < (JsonSerializer.SerializeToUtf8Bytes(pending.Request, PlanningJsonContext.Default.LLMRequest).LongLength + 2) / 3 + 256 ||
            journal["output_ceiling"]?.GetValue<long>() is not { } outputCeiling || outputCeiling < pending.Request.MaxTokens ||
            journal["cost_ceiling_eur"]?.GetValue<decimal>() is not >= 0 ||
            !ValidRetainedQuote(journal["exchange_quote"] as JsonObject) ||
            journal["usage"] is not null ||
            await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null)
            throw new InvalidOperationException("Permanent retention requires matching unknown transport evidence and a complete unchanged reservation.");
        var source = (run["source"] ?? result?["source_commit"])?.ToString();
        string? configurationHash = null;
        if (interrupted)
        {
            // Corpus source/cohort identity exists in the durable key before a final result.
            // Reconstruct only that key, never a missing failure, response or workflow state.
            var parts = runKey.Split(':');
            if (parts.Length < 4 || parts[0].Length != 40 || !parts[0].All(Uri.IsHexDigit) ||
                parts[1] is not ("fixture" or "pilot" or "measured") ||
                state.Request.SessionId != PlanningGraphCompiler.Fingerprint(Id + ":" + runKey))
                throw new InvalidOperationException("The interrupted corpus identity does not match its original session.");
            var index = 2;
            var arm = parts[index] is "judge" or "defaults" ? parts[index++] : null;
            string? cohort = null;
            if (parts[index] == "cohort")
            {
                if (++index >= parts.Length) throw new InvalidOperationException("The original corpus cohort is incomplete.");
                cohort = PlanningBenchmarkMeasurements.Cohort(["--cohort", parts[index++]]);
            }
            if (index + 2 != parts.Length || parts[index] != state.Request.Name ||
                !int.TryParse(parts[index + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var repetition) ||
                repetition is < 1 or > 20 || runKey != PlanningBenchmarkMeasurements.CorpusRunKey(parts[0], parts[1], arm, cohort, state.Request.Name, repetition) ||
                source is not null && source != parts[0])
                throw new InvalidOperationException("The interrupted corpus source, case or repetition changed.");
            source = parts[0];
            var configuration = await LoadAsync("planning-evaluation-configuration",
                "corpus-settings:" + source + ":" + parts[1] + ":" + (arm ?? "control") + (cohort is null ? "" : ":cohort:" + cohort), ct);
            if (configuration is null || !JsonNode.DeepEquals(configuration, PlanningBenchmarkMeasurements.Configuration(state.Request)))
                throw new InvalidOperationException("The interrupted corpus settings do not match the frozen configuration.");
            configurationHash = Hash(configuration);
        }
        if (source is null || source.Length != 40 || !source.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The original source identity is required.");
        if (run["source"] is not null && result?["source_commit"] is not null && run["source"]!.ToString() != result?["source_commit"]!.ToString() ||
            run["manifest"] is JsonObject manifest &&
                (manifest["production_sha"]?.ToString() != source || manifest["harness_sha"]?.ToString() != source))
            throw new InvalidOperationException("The retained source and run manifest disagree.");
        JsonObject? frozenArm = null;
        if (frozenProof is not null)
        {
            var originals = (await Records.ListAsync("planning-evaluation-requests", "benchmark", Author, ct))
                .Where(row => row.Key.StartsWith(Id + ":" + state.Request.SessionId + ":", StringComparison.Ordinal)).ToArray();
            if (originals.Length == 0) throw new InvalidOperationException("The original pre-dispatch request record is missing.");
            var configuration = await LoadAsync("planning-evaluation-configuration", "configuration", ct)
                ?? throw new InvalidOperationException("The original provider configuration is missing.");
            frozenArm = BenchmarkFrozenBaselineArm.ValidateRun(frozenProof, Id, collection, runKey, source, state,
                run["session"]!["request"]!.AsObject(), request, policy, journal, retry, configuration, originals.Min(row => row.CreatedAt));
        }
        if (journal["effective_transport_policy"] is JsonObject effective &&
            (journal["effective_transport_policy_hash"]?.ToString() != Hash(effective) ||
             effective["allow_http_recovery"]?.GetValue<bool>() != capturedRetries ||
             !capturedRetries && frozenArm is null && (effective["version"]?.GetValue<int>() != 1 || effective["disable_transport_retries"]?.GetValue<bool>() != true ||
             effective["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1)))
            throw new InvalidOperationException("The issued transport policy permits recovery; this terminal operator cannot reinterpret it.");
        if (journal["effective_transport_policy"] is null && journal["effective_transport_policy_hash"] is not null ||
            (capturedRetries || interrupted || retentionKind is SingleHttp500Retention or SingleHttp502Retention or SingleHttp504Retention) && journal["effective_transport_policy"] is not JsonObject)
            throw new InvalidOperationException("The captured effective transport policy is missing.");
        var evidence = new JsonObject
        {
            ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = collection,
            ["run_key"] = runKey, ["session_id"] = state.Request.SessionId, ["revision"] = state.Revision,
            ["request_id"] = requestId, ["phase"] = pending.Purpose, ["source"] = source,
            ["run_hash"] = Hash(run), ["request_hash"] = Hash(request), ["http_journal_hash"] = Hash(journal),
            ["failure_hash"] = failure is null ? null : Hash(failure), ["session_policy_hash"] = Hash(policy),
            ["campaign_retry_policy_hash"] = Hash(retry),
            ["financial_admission_hash"] = Hash(new JsonObject { ["input_ceiling"] = inputCeiling, ["output_ceiling"] = outputCeiling,
                ["cost_ceiling_eur"] = journal["cost_ceiling_eur"]!.DeepClone(), ["exchange_quote"] = journal["exchange_quote"]!.DeepClone() }),
            ["effective_transport_policy_hash"] = journal["effective_transport_policy"] is JsonObject transport ? Hash(transport) : null,
            ["transport_policy_evidence"] = journal["effective_transport_policy"] is null
                ? "retained_incremental_session_policy_and_original_source" : "captured_effective_request_policy",
            ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, requestId, ct: ct)
        };
        if (retentionKind is not null) evidence["retention_kind"] = retentionKind;
        if (frozenArm is not null) evidence["frozen_arm"] = frozenArm;
        if (interrupted)
        {
            evidence.Remove("failure_hash");
            evidence["outcome_evidence"] = "reserved_attempt_without_persisted_outcome";
            evidence["source_evidence"] = "corpus_key_and_frozen_configuration";
            evidence["configuration_hash"] = configurationHash;
        }
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    // The caller holds the campaign lease. Inspect is read-only; apply publishes exactly one
    // immutable closure. Neither path constructs a provider, alters evidence or settles usage.
    internal async Task<JsonObject> ApplyPermanentRetentionAsync(string collection, string runKey, string requestId,
        string expectedEvidenceHash, string operatorIdentity, string authorization, CancellationToken ct, string? retentionKind = null,
        BenchmarkFrozenBaselineArm? frozenBaseline = null)
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
            BenchmarkFrozenBaselineArm.RequireSame(existing["evidence"]?["frozen_arm"], frozenBaseline?.Read());
            if (existing["reason"]?.ToString() != PermanentRetentionReason ||
                existing["evidence_hash"]?.ToString() != expectedEvidenceHash ||
                existing["authorization_hash"]?.ToString() != authorizationHash ||
                existing["evidence"]?["run_collection"]?.ToString() != collection ||
                existing["evidence"]?["run_key"]?.ToString() != runKey ||
                existing["evidence"]?["retention_kind"]?.ToString() != retentionKind)
                throw new InvalidOperationException("The existing closure is immutable and does not match this authorization.");
            return existing;
        }
        var inspected = await InspectPermanentRetentionAsync(collection, runKey, requestId, ct, retentionKind, frozenBaseline);
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
            evidence["retention_kind"]?.ToString() is not (null or SingleHttp500Retention or SingleHttp502Retention or SingleHttp504Retention or InterruptedProcessRetention or ExhaustedPlanningRetriesRetention) ||
            evidence["retention_kind"]?.ToString() == InterruptedProcessRetention &&
                (evidence.ContainsKey("failure_hash") || evidence["outcome_evidence"]?.ToString() != "reserved_attempt_without_persisted_outcome" ||
                 evidence["source_evidence"]?.ToString() != "corpus_key_and_frozen_configuration" ||
                 evidence["configuration_hash"]?.ToString() is not { Length: 64 } configurationHash || !configurationHash.All(Uri.IsHexDigit)) ||
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
        if (retentionKind is not (null or SingleHttp500Retention or SingleHttp502Retention or SingleHttp504Retention or InterruptedProcessRetention or ExhaustedPlanningRetriesRetention))
            throw new ArgumentException("Choose the exact single-http-500, single-http-502, single-http-504 or interrupted-process or exhausted-planning-retries retention kind.", nameof(retentionKind));
    }

    private static bool MatchesCapturedPlanningFailure(PlanningSession state, JsonObject policy, JsonObject journal, JsonArray attempts, JsonObject? failure)
    {
        var captured = PlanningTransportPolicies.Read(state.Request);
        var effective = journal["effective_transport_policy"] as JsonObject;
        var retry = effective?["effective_retry_policy"];
        if (captured is null || effective?["version"]?.GetValue<int>() != 2 ||
            !JsonNode.DeepEquals(policy[PlanningTransportPolicies.OptionName], state.Request.Options[PlanningTransportPolicies.OptionName]) ||
            !JsonNode.DeepEquals(effective["planning_transport_policy"], policy[PlanningTransportPolicies.OptionName]) ||
            !JsonNode.DeepEquals(policy["benchmark_planning_elapsed_ms"], state.Request.Options["benchmark_planning_elapsed_ms"]) ||
            !JsonNode.DeepEquals(policy["llm_budget"], state.Request.Options["llm_budget"]) ||
            state.Request.Options["construction_presentation_version"]?.ToString() is "6" or "7" or "8" or "9" or "10" or "11" &&
                !JsonNode.DeepEquals(policy["construction_presentation_version"], state.Request.Options["construction_presentation_version"]) ||
            state.PendingCall!.Request.UseBackgroundMode || !PlanningTransportPolicies.ReviewHeadroomValid(state.PendingCall.Request, state.Request) ||
            effective["disable_transport_retries"]?.GetValue<bool>() != state.PendingCall.Request.DisableTransportRetries ||
            retry?["MaxAttempts"]?.GetValue<int>() != (state.PendingCall.Request.DisableTransportRetries ? 1 : captured.MaxAttempts) ||
            retry["MaxUncertainRetries"]?.GetValue<int>() != 0 ||
            retry["AttemptTimeoutMilliseconds"]?.GetValue<int>() != captured.AttemptTimeoutMilliseconds ||
            retry["BaseDelayMilliseconds"]?.GetValue<int>() != captured.BaseDelayMilliseconds ||
            retry["MaxDelayMilliseconds"]?.GetValue<int>() != captured.MaxDelayMilliseconds ||
            retry["MaxTotalDelayMilliseconds"]?.GetValue<int>() != captured.MaxTotalDelayMilliseconds ||
            attempts.Count > retry["MaxAttempts"]!.GetValue<int>() ||
            failure?["stage"]?.ToString() != "dispatch" || failure["attempt_count"]?.GetValue<int>() != attempts.Count ||
            attempts.Select(a => a?["Id"]?.ToString()).Distinct(StringComparer.Ordinal).Count() != attempts.Count ||
            attempts.Any(a => a?["Id"]?.ToString() is not { Length: > 0 } id || !id.StartsWith(state.PendingCall.Id + ":", StringComparison.Ordinal))) return false;
        for (var i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i]!;
            if (attempt["Status"] is null)
                return i == attempts.Count - 1 && attempt["Body"] is null && attempt["Failure"]?.ToString() is "timeout" or "transport" &&
                    failure["status_code"] is null && failure["kind"]?.ToString() is "Timeout" or "Transport";
            if (attempt["Status"]!.GetValue<int>() is not (429 or 500 or 502 or 503 or 504) ||
                attempt["Failure"] is not null || !IsRetainedErrorOnlyBody(attempt["Body"]?.ToString())) return false;
        }
        return failure["retry_exhausted"]?.GetValue<bool>() == true && failure["retryable"]?.GetValue<bool>() == true &&
            failure["status_code"]?.GetValue<int>() == attempts[^1]!["Status"]!.GetValue<int>() &&
            failure["kind"]?.ToString() is "RateLimited" or "ServiceUnavailable" or "Timeout";
    }

    private static bool MatchesPermanentFailure(JsonObject failure, JsonNode? attempt, string? retentionKind)
        => retentionKind is SingleHttp500Retention or SingleHttp502Retention or SingleHttp504Retention
            ? failure["kind"]?.ToString() == (retentionKind == SingleHttp504Retention ? "Timeout" : "ServiceUnavailable") &&
              failure["status_code"]?.GetValue<int>() == RetentionHttpStatus(retentionKind) &&
              attempt?["Status"]?.GetValue<int>() == RetentionHttpStatus(retentionKind) && attempt["Failure"] is null &&
              attempt["Body"] is JsonValue body && body.TryGetValue<string>(out var text) && IsRetainedErrorOnlyBody(text)
            : failure["kind"]?.ToString() is "Timeout" or "Transport" && failure["status_code"] is null &&
              attempt?["Status"] is null && attempt?["Body"] is null && attempt?["Failure"]?.ToString() is "timeout" or "transport";

    private static int RetentionHttpStatus(string kind) => kind switch
    {
        SingleHttp500Retention => 500,
        SingleHttp502Retention => 502,
        SingleHttp504Retention => 504,
        _ => throw new ArgumentException("An explicit supported HTTP retention kind is required.", nameof(kind))
    };

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
