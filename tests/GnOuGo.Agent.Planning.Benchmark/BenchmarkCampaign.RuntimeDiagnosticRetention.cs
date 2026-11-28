using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

internal sealed partial class BenchmarkCampaign
{
    internal const string RuntimeDiagnosticRetention = "tool-free-runtime-diagnostic";

    private async Task<JsonObject> InspectRuntimeDiagnosticRetentionAsync(string collection, string runKey, string requestId, CancellationToken ct)
    {
        var run = await LoadAsync(collection, runKey, ct) ?? throw new InvalidOperationException("The exact diagnostic run is missing.");
        if (collection != "planning-evaluation-runs" || run["phase"]?.ToString() != "isolated_source_consumer" ||
            run["run"]?.ToString() != runKey || !requestId.StartsWith(runKey + "-execution:", StringComparison.Ordinal) ||
            run["passed"]?.GetValue<bool>() != false || run["durable_receipt_reloaded"]?.GetValue<bool>() != false ||
            run["actual_navigation"]?.GetValue<int>() != 0 || run["business_execution"]?.GetValue<bool>() != false ||
            run["fragment_applied"]?.GetValue<bool>() != false || run["full_workflow_oracle_passed"]?.GetValue<bool>() != false ||
            run["original_session_modified"]?.GetValue<bool>() != false || run["exception_type"]?.ToString() != typeof(LLMClientException).FullName ||
            !DateTimeOffset.TryParse(run["started_utc"]?.ToString(), out var started) ||
            !DateTimeOffset.TryParse(run["finished_utc"]?.ToString(), out var finished) || finished < started ||
            !Hex(run["source"], 40) || !Hex(run["harness_sha256"], 64) || !Hex(run["input_template_sha256"], 64) ||
            new[] { "host", "session", "session_id", "artifact_hash", "approved_hash", "execution_id", "execution_run_id",
                "execution_started", "execution", "final_review", "events", "task_receipts", "response", "usage" }.Any(k => run[k] is not null))
            throw new InvalidOperationException("Retention requires a finished isolated diagnostic with no response, planning or business effects.");
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct) ?? throw new InvalidOperationException("The exact diagnostic request is missing.");
        var logical = request.Deserialize(PlanningJsonContext.Default.LLMRequest) ?? throw new InvalidOperationException("The diagnostic request is invalid.");
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct) ?? throw new InvalidOperationException("The diagnostic HTTP journal is missing.");
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct) ?? throw new InvalidOperationException("The captured diagnostic failure is missing.");
        var configurationId = run["campaign_configuration"]?.ToString();
        var configuration = configurationId is { Length: 64 } ? await LoadAsync("planning-evaluation-configuration", "configuration:" + configurationId, ct) : null;
        var transport = journal["effective_transport_policy"] as JsonObject;
        var physical = journal["transport"] as JsonObject;
        if (!JsonNode.DeepEquals(run["request"], request) || logical.ClientRequestId != requestId ||
            logical.Tools is { Count: > 0 } || logical.UseBackgroundMode || !logical.DisableTransportRetries ||
            logical.MaxTokens is not (> 0 and <= 32768) || logical.RequiredReviewCalls is not null || logical.StructuredOutputSchema is null || logical.StructuredOutputStrict != true ||
            await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null || journal["usage"] is not null ||
            transport?["version"]?.GetValue<int>() != 2 || transport["mode"]?.ToString() != "runtime_single_attempt" ||
            transport["allow_http_recovery"]?.GetValue<bool>() != true || transport["disable_transport_retries"]?.GetValue<bool>() != true ||
            transport["request_hash"]?.ToString() != Hash(request) || journal["effective_transport_policy_hash"]?.ToString() != Hash(transport) ||
            transport["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1 || transport["effective_retry_policy"]?["MaxUncertainRetries"]?.GetValue<int>() != 0 ||
            physical?["Attempts"] is not JsonArray { Count: 1 } attempts || attempts[0] is not JsonObject attempt ||
            attempt["Id"]?.ToString() is not { Length: > 0 } attemptId || !attemptId.StartsWith(requestId + ":", StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(attempt["StartedAtUtc"]?.ToString(), out var attemptStarted) ||
            !DateTimeOffset.TryParse(attempt["CompletedAtUtc"]?.ToString(), out var attemptEnded) ||
            attemptStarted < started || attemptEnded < attemptStarted || attemptEnded > finished ||
            attempt["Status"] is not null || attempt["Body"] is not null || attempt["AssembledBody"] is not null ||
            attempt["ResponseBodyComplete"]?.GetValue<bool>() != false || attempt["ProtocolComplete"]?.GetValue<bool>() == true ||
            attempt["UsageKnown"]?.GetValue<bool>() == true || attempt["Failure"]?.ToString() is not ("transport" or "timeout") ||
            failure["stage"]?.ToString() != "dispatch" || failure["kind"]?.ToString() is not ("Transport" or "Timeout") ||
            failure["status_code"] is not null || failure["attempt_count"]?.GetValue<int>() != 1 ||
            journal["input_ceiling"]?.GetValue<long>() is not > 0 || journal["output_ceiling"]?.GetValue<long>() != logical.MaxTokens ||
            journal["cost_ceiling_eur"]?.GetValue<decimal>() is not > 0 || journal["exchange_quote"] is not JsonObject ||
            configuration?["configuration"] is not JsonObject frozen || frozen["provider"]?.ToString() != logical.Provider || frozen["model"]?.ToString() != logical.Model ||
            !(await CheckConfigurationSelectionAsync(configurationId!, frozen, configuration["host_configuration_hash"]!.ToString(), ct)).Accepted)
            throw new InvalidOperationException("Receipt, response, ownership, policy, configuration or reservation evidence excludes diagnostic retention.");
        var body = physical["RequestBody"]?.GetValue<string>();
        var actual = body is null ? null : JsonNode.Parse(body) as JsonObject;
        if (physical["RequestMethod"]?.ToString() != "POST" || actual is null || actual["model"]?.ToString() != logical.Model ||
            actual["max_completion_tokens"]?.GetValue<int>() != logical.MaxTokens || actual["background"]?.GetValue<bool>() == true ||
            actual["max_tokens"] is not null || actual["max_output_tokens"] is not null ||
            actual["tools"] is not null and not JsonArray { Count: 0 } || actual["tool_choice"] is not null ||
            actual["functions"] is not null || actual["function_call"] is not null ||
            actual["response_format"]?["type"]?.ToString() != "json_schema" || actual["response_format"]?["json_schema"]?["strict"]?.GetValue<bool>() != true ||
            !JsonNode.DeepEquals(actual["response_format"]?["json_schema"]?["schema"], logical.StructuredOutputSchema) ||
            actual["messages"] is not JsonArray messages || !messages.Any(m => m?["role"]?.ToString() == "user" && m["content"]?.ToString() == logical.Prompt))
            throw new InvalidOperationException("The exact bounded tool-free foreground wire request is not established.");
        foreach (var owner in new[] { "agent-planning-model-requests-v11", "agent-planning-model-receipts-v11" })
            if (await Records.GetAsync(owner, "benchmark", runKey + ":" + requestId, Author, ct) is not null)
                throw new InvalidOperationException("Planning-owned evidence requires its existing session recovery path.");
        var evidence = new JsonObject { ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = collection,
            ["run_key"] = runKey, ["session_id"] = runKey + "-execution", ["request_id"] = requestId, ["phase"] = "isolated_source_consumer",
            ["source"] = run["source"]!.DeepClone(), ["retention_kind"] = RuntimeDiagnosticRetention, ["run_hash"] = Hash(run), ["request_hash"] = Hash(request),
            ["http_journal_hash"] = Hash(journal), ["failure_hash"] = Hash(failure), ["configuration_hash"] = Hash(configuration),
            ["effective_transport_policy_hash"] = Hash(transport), ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, ct: ct),
            ["request_accounting"] = LiveCampaignEvidence.Summarize([journal]), ["outcome_evidence"] = "recorded_transport_loss_without_completion_receipt",
            ["source_evidence"] = "tool_free_runtime_diagnostic_and_frozen_configuration", ["no_business_effects"] = true };
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };

        static bool Hex(JsonNode? value, int length) => value?.ToString() is { } text && text.Length == length && text.All(Uri.IsHexDigit);
    }

    private static bool ValidRuntimeDiagnosticEvidence(JsonObject evidence) =>
        evidence["run_collection"]?.ToString() == "planning-evaluation-runs" && evidence["phase"]?.ToString() == "isolated_source_consumer" &&
        evidence["session_id"]?.ToString() == evidence["run_key"]?.ToString() + "-execution" &&
        evidence["source_evidence"]?.ToString() == "tool_free_runtime_diagnostic_and_frozen_configuration" &&
        evidence["outcome_evidence"]?.ToString() == "recorded_transport_loss_without_completion_receipt" && evidence["no_business_effects"]?.GetValue<bool>() == true &&
        evidence["configuration_hash"]?.ToString() is { Length: 64 } && evidence["failure_hash"]?.ToString() is { Length: 64 } &&
        evidence["request_accounting"] is JsonObject;
}
