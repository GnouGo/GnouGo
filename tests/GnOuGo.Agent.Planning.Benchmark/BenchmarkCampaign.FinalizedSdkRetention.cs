using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Persistence;

internal sealed partial class BenchmarkCampaign
{
    internal const string FinalizedSdkRetention = "finalized-server-sdk-request";

    private static async Task<EncryptedWorkflowRunStore.RunEvidence> OpenFinalizedExecutionAsync(JsonObject report, string workspace, CancellationToken ct)
    {
        if (report["execution_run_id"]?.GetValue<string>() is not { } id || !Guid.TryParseExact(id, "N", out _))
            throw new InvalidOperationException("The Server report does not identify a workflow execution.");
        var store = LiveWorkflowEvaluation.ExecutionStore(workspace);
        return await store.OpenEvidenceAsync("benchmark", id, ct);
    }

    private async Task<JsonObject> InspectFinalizedSdkAsync(JsonObject report, ServerRetentionScope scope, WorkflowRun execution, string id, CancellationToken ct, string? sdkApiVersion)
    {
        var session = await scope.Store.ReadEvidenceAsync("benchmark", scope.Session, ct) ?? throw new InvalidOperationException("The saved planning session is missing.");
        var prefix = scope.Session + "-copilot:";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || !IsSha256(id[prefix.Length..]) ||
            report["host"]?.ToString() != "GnOuGo.Agent.Server" || report["phase"]?.ToString() != "server_planning" ||
            report["budget_mode"]?.ToString() != "limited" || report["passed"]?.GetValue<bool>() != false ||
            report["execution"]?["status"]?.ToString() != WorkflowRunStatus.Failed || report["execution"]?["success"]?.GetValue<bool>() != false ||
            report["execution"]?["finalization_completed"]?.GetValue<bool>() != true || report["finished_utc"] is null ||
            session.Request.SessionId != scope.Session || session.Request.TenantId != "benchmark" || session.Status != PlanningStatus.Saved ||
            !IsSha256(session.ApprovedHash) || session.ApprovedHash != report["execution_artifact_hash"]?.ToString() ||
            session.SavedAgentId is null || session.SavedAgentId != report["saved_agent_id"]?.ToString() || session.SavedAgentId != execution.Limits.AgentId ||
            execution.TenantId != "benchmark" || report["execution_run_id"]?.ToString() != execution.RunId ||
            string.IsNullOrEmpty(session.Yaml) || execution.WorkflowYaml != session.Yaml || !ServerBusinessExecution.MatchesSubmittedInputs(report["inputs"], execution.Inputs))
            throw new InvalidOperationException("Retention requires the original saved artifact and its finished failed Server execution.");

        var configurationId = report["campaign_configuration"]?.ToString();
        var selected = IsSha256(configurationId) ? await LoadAsync("planning-evaluation-configuration", "configuration:" + configurationId, ct) : null;
        if (selected?["configuration"] is not JsonObject frozen || !(await CheckConfigurationSelectionAsync(configurationId!, frozen,
            selected["host_configuration_hash"]!.ToString(), ct)).Accepted || frozen["provider"]?.ToString() != report["provider"]?.ToString() ||
            frozen["model"]?.ToString() != report["model"]?.ToString())
            throw new InvalidOperationException("The funded deployment must match the recorded Server configuration.");

        var issued = await OwnedKeysAsync("planning-evaluation-requests", ct, prefix);
        if (issued.Count != 1 || issued[0] != Id + ":" + id)
            throw new InvalidOperationException("The historical protocol requires a unique SDK request in this session.");
        if (await LoadAsync("planning-evaluation-receipts", id, ct) is not null)
            throw new InvalidOperationException("A received inference requires its existing recovery path.");
        var request = await LoadAsync("planning-evaluation-requests", id, ct) ?? throw new InvalidOperationException("The issued SDK request is missing.");
        var storedHttp = await ReadRecordAsync(BenchmarkHttpJournal.Collection, id, ct) ?? throw new InvalidOperationException("The SDK HTTP journal is missing.");
        var storedFailure = await ReadRecordAsync("planning-evaluation-failures", id, ct) ?? throw new InvalidOperationException("The SDK failure is missing.");
        var http = JsonNode.Parse(storedHttp.Value)!.AsObject();
        var failure = JsonNode.Parse(storedFailure.Value)!.AsObject();
        if (failure["stage"]?.ToString() != "dispatch" || failure["exception_type"]?.ToString() != "HttpRequestException" ||
            failure["status_code"] is not null || failure["dispatch_status"] is not null)
            throw new InvalidOperationException("The recorded failure does not identify an unresolved transport dispatch.");

        var money = VerifyUnreceivedSdkWire(id, request, http, frozen, sdkApiVersion);
        var effects = FinalizedSdkEffects(execution, report["events"] as JsonArray, storedHttp.UpdatedAt, storedFailure.CreatedAt);
        var evidence = new JsonObject
        {
            ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = "planning-evaluation-runs",
            ["run_key"] = report["run"]!.DeepClone(), ["session_id"] = scope.Session, ["request_id"] = id,
            ["retention_kind"] = FinalizedSdkRetention, ["source"] = report["source"]!.DeepClone(),
            ["execution_run_id"] = execution.RunId, ["execution_revision"] = execution.Revision, ["artifact_hash"] = session.ApprovedHash,
            ["reservation_recorded_utc"] = storedHttp.UpdatedAt.ToUniversalTime(), ["failure_recorded_utc"] = storedFailure.CreatedAt.ToUniversalTime(),
            ["run_hash"] = Hash(report), ["execution_inputs_hash"] = Hash(execution.Inputs!.AsObject()), ["configuration_hash"] = Hash(selected), ["failure_hash"] = Hash(failure),
            ["request_hash"] = Hash(request), ["http_journal_hash"] = Hash(http), ["effects"] = effects,
            ["request_accounting"] = money, ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, ct: ct),
            ["outcome_evidence"] = "terminal_workflow_with_unreceived_text_inference"
        };
        return new() { ["evidence"] = evidence, ["evidence_hash"] = FinalizedSdkEvidenceHash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    internal static JsonObject VerifyUnreceivedSdkWire(string id, JsonObject request, JsonObject http, JsonObject frozen, string? sdkApiVersion = null)
    {
        var logical = request.Deserialize(PlanningJsonContext.Default.LLMRequest)!;
        var policy = http["effective_transport_policy"];
        var wire = http["transport"];
        if (logical.ClientRequestId != id || logical.Provider != frozen["provider"]?.ToString() || logical.Model != frozen["model"]?.ToString() ||
            logical.Tools is { Count: > 0 } || logical.UseBackgroundMode || logical.MaxTokens is not (> 0 and <= 32768) ||
            policy is not JsonObject capture || capture["mode"]?.ToString() != "sdk_proxy_single_attempt" ||
            capture["sdk_inference_transport"]?.ToString() != SdkStructuredTransport || capture["version"]?.GetValue<int>() != 2 ||
            capture["disable_transport_retries"]?.GetValue<bool>() != true || capture["allow_http_recovery"]?.GetValue<bool>() != true ||
            capture["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1 || capture["effective_retry_policy"]?["MaxUncertainRetries"]?.GetValue<int>() != 0 ||
            capture["request_hash"]?.ToString() != Hash(request) || http["effective_transport_policy_hash"]?.ToString() != Hash(capture) ||
            !IsSha256(capture["endpoint_sha256"]?.ToString()) || !IsSha256(capture["wire_fingerprint"]?.ToString()) ||
            capture["wire_fingerprint"]?.ToString() != wire?["Fingerprint"]?.ToString() || wire?["RequestMethod"]?.ToString() != "POST" ||
            wire["RequestBody"]?.GetValue<string>() is not { } text || logical.Prompt != text ||
            !Uri.TryCreate(wire["Endpoint"]?.ToString(), UriKind.Absolute, out var endpoint) || !Uri.TryCreate(frozen["endpoint"]?.ToString(), UriKind.Absolute, out var origin) ||
            endpoint.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) ||
            !endpoint.AbsolutePath.StartsWith(origin.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal) || !endpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ||
            wire["Attempts"] is not JsonArray { Count: 1 } attempts || attempts[0] is not JsonObject attempt || attempt["Id"]?.ToString() != id ||
            attempt.Any(p => p.Key != "Id" && p.Value is not null) || http["usage"] is not null ||
            http["input_ceiling"]?.GetValue<long>() is not > 0 || http["output_ceiling"]?.GetValue<long>() != logical.MaxTokens ||
            http["cost_ceiling_eur"]?.GetValue<decimal>() is not > 0 || http["exchange_quote"] is not JsonObject)
            throw new InvalidOperationException("The exact bounded SDK request, unknown outcome and reservation must agree.");
        // The HTTP journal deliberately strips the query. An API version is only
        // a candidate for reconstructing the original URI. Both digests must match.
        var originalUri = endpoint.AbsoluteUri;
        if (sdkApiVersion is not null)
        {
            if (sdkApiVersion.Length is 0 or > 64 || sdkApiVersion.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.')) ||
                endpoint.Query.Length != 0)
                throw new InvalidOperationException("An SDK API version must be a single bounded query value.");
            originalUri = new UriBuilder(endpoint) { Query = "api-version=" + sdkApiVersion }.Uri.AbsoluteUri;
        }
        if (capture["endpoint_sha256"]?.ToString() != PlanningGraphCompiler.Fingerprint(originalUri) ||
            capture["wire_fingerprint"]?.ToString() != PlanningGraphCompiler.Fingerprint("POST\n" + originalUri + "\n" + text))
            throw new InvalidOperationException("The reconstructed SDK URI and request body must match both original digests.");
        // Only text generation can remain uncertain. Provider-hosted tools, continuations and background work are ineligible.
        if (JsonNode.Parse(text) is not JsonObject body || body.Any(p => p.Key is not
                ("model" or "max_completion_tokens" or "reasoning_effort" or "messages" or "response_format" or "temperature" or "stream")) ||
            body["model"]?.ToString() != logical.Model || body["max_completion_tokens"]?.GetValue<int>() != logical.MaxTokens ||
            body["messages"] is not JsonArray { Count: > 0 } || body["response_format"]?["type"]?.ToString() != "json_schema")
            throw new InvalidOperationException("The captured provider request must be a foreground text-only SDK decision.");
        var money = LiveCampaignEvidence.Summarize([http]);
        if (money["unknown_attempts"]?.GetValue<long>() != 1 || money["reserved_cost_eur"]?.GetValue<decimal>() != http["cost_ceiling_eur"]!.GetValue<decimal>())
            throw new InvalidOperationException("The full unknown-usage reservation must remain chargeable.");
        return money;
    }

    internal static JsonArray FinalizedSdkEffects(WorkflowRun run, JsonArray? events, DateTimeOffset reservedAt, DateTimeOffset failureAt)
    {
        if (run.Status != WorkflowRunStatus.Failed || run.Result?.Success != false || !run.FinalizationStarted || !run.FinalizationCompleted ||
            run.Invocations.Values.Any(i => i.Status is not ("completed" or "failed")) || events is null)
            throw new InvalidOperationException("All invocations must be terminal and cleanup must have finished.");
        var external = run.Invocations.Values.Where(i => i.Recovery == StepRecovery.External).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
        var failed = external.Where(i => i.Status == "failed").ToArray();
        if (failed.Length != 1 || external.Count(i => i.ResolvedInput?["server"]?.ToString() == SdkServer) != 1 ||
            failed[0].Error?.Code != "MCP_CALL_ERROR" || failed[0].StepType != "mcp.call" ||
            failed[0].ResolvedInput?["server"]?.ToString() != SdkServer || failed[0].ResolvedInput?["method"]?.ToString() != "code_agent_edit" ||
            failed[0].Observation?["status"]?.ToString() != "error" ||
            failed[0].Observation?["response"] is not JsonObject result || result["success"]?.GetValue<bool>() != false ||
            result["error_code"]?.ToString() != "COPILOT_TURN_FAILED" || result["usageJson"] is not null ||
            result["toolExecutions"] is not JsonArray { Count: 0 } || result["modifiedFiles"] is not JsonArray { Count: 0 } ||
            failed[0].DispatchedAt is not { } start || failed[0].CompletedAt is not { } end ||
            reservedAt < start || failureAt < reservedAt || failureAt > end)
            throw new InvalidOperationException("The single failed SDK invocation must cover the reservation and failure and attest zero executed tools.");
        var effects = new JsonArray();
        var unmatched = events.OfType<JsonObject>().ToList();
        if (unmatched.Count != events.Count) throw new InvalidOperationException("An MCP event is invalid.");
        foreach (var i in external)
        {
            if (!i.ExternalCompletionObserved || i.DispatchedAt is null || i.CompletedAt is null || i.Observation is null ||
                i.StepType is not ("mcp.call" or "llm.call") || i.IsFinalization && i.Status != "completed")
                throw new InvalidOperationException("An external invocation has an unverified outcome.");
            if (i.StepType == "mcp.call")
            {
                var input = i.ResolvedInput;
                var matches = unmatched.Where(e => e["server"]?.ToString() == input?["server"]?.ToString() &&
                    e["tool"]?.ToString() == input?["method"]?.ToString() && e["error"]?.GetValue<bool>() == (i.Status == "failed") &&
                    e["started"]?.GetValue<DateTimeOffset>() is { } time && time >= i.DispatchedAt && time <= i.CompletedAt &&
                    JsonNode.DeepEquals(e["arguments"], input?["request"]) && JsonNode.DeepEquals(e["result"], i.Observation["response"])).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Each observed MCP effect requires its own exact event and receipt.");
                unmatched.Remove(matches[0]);
            }
            effects.Add(new JsonObject { ["invocation"] = i.Id, ["type"] = i.StepType, ["status"] = i.Status, ["finalization"] = i.IsFinalization,
                ["server"] = i.ResolvedInput?["server"]?.DeepClone(), ["method"] = i.ResolvedInput?["method"]?.DeepClone(),
                ["receipt_sha256"] = PlanningGraphCompiler.Fingerprint(i.Observation.ToJsonString()) });
        }
        if (unmatched.Count != 0) throw new InvalidOperationException("An MCP effect is missing from the execution journal.");
        return effects;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal static string FinalizedSdkEvidenceHash(JsonObject evidence)
    {
        // The issuer serialized typed UTC timestamps. A reloaded JsonNode writes
        // their '+' as a Unicode escape unless we restore the original scalar type.
        var material = evidence.DeepClone().AsObject();
        foreach (var key in new[] { "reservation_recorded_utc", "failure_recorded_utc" })
        {
            if (material[key] is not JsonValue date || !date.TryGetValue<DateTimeOffset>(out var timestamp) || timestamp.Offset != TimeSpan.Zero ||
                date.TryGetValue<string>(out var encoded) && JsonSerializer.SerializeToElement(timestamp).GetString() != encoded)
                throw new InvalidOperationException("The original canonical SDK journal timestamps are required.");
            material[key] = timestamp;
        }
        return Hash(material);
    }

    private static bool ValidFinalizedSdkEvidence(JsonObject evidence) =>
        evidence["run_collection"]?.ToString() == "planning-evaluation-runs" && Guid.TryParseExact(evidence["execution_run_id"]?.ToString(), "N", out _) &&
        evidence["execution_revision"]?.GetValue<long>() is > 0 && IsSha256(evidence["artifact_hash"]?.ToString()) &&
        evidence["outcome_evidence"]?.ToString() == "terminal_workflow_with_unreceived_text_inference" && evidence["effects"] is JsonArray { Count: > 0 } &&
        evidence["request_accounting"]?["unknown_attempts"]?.GetValue<long>() == 1 && evidence["request_accounting"]?["reserved_cost_eur"]?.GetValue<decimal>() > 0;
}
