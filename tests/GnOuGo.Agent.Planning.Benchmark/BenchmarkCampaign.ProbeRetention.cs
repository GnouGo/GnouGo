using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

internal sealed partial class BenchmarkCampaign
{
    internal const string ProviderProbeRetention = "provider-probe";

    private async Task<JsonObject> InspectProviderProbeRetentionAsync(string collection, string runKey, string requestId, CancellationToken ct)
    {
        var run = await LoadAsync(collection, runKey, ct) ?? throw new InvalidOperationException("The exact independent probe run is missing.");
        if (collection != "planning-evaluation-runs" || run["host"]?.ToString() != "GnOuGo.Agent.Server" || run["phase"]?.ToString() != "provider_probe" ||
            run["run"]?.ToString() != runKey || run["campaign"]?.ToString() != Id || run["request_id"]?.ToString() != requestId ||
            !requestId.StartsWith(runKey + ":probe:", StringComparison.Ordinal) || run["passed"]?.GetValue<bool>() != false ||
            run["frontend_ready"]?.GetValue<bool>() != true || run["budget_mode"]?.ToString() != "limited" || run["finished_utc"] is null || run["durable_receipt"]?.GetValue<bool>() != false ||
            run["physical_http_attempts"]?.GetValue<int>() != 1 || run["independent_probe"] is not JsonObject independent ||
            independent["applies_candidate"]?.GetValue<bool>() != false || independent["executes_business_tools"]?.GetValue<bool>() != false ||
            new[] { "session", "session_id", "artifact_hash", "approved_hash", "execution_id", "execution_run_id", "execution_started", "execution", "final_review" }.Any(k => run[k] is not null) ||
            run["source"]?.ToString() is not { Length: 40 } source || !source.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Retention requires a finished independent Server generation probe with no planning, approved artifact or business execution.");
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct) ?? throw new InvalidOperationException("The exact probe request is missing.");
        var logical = request.Deserialize(PlanningJsonContext.Default.LLMRequest) ?? throw new InvalidOperationException("The exact probe request is invalid.");
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct) ?? throw new InvalidOperationException("The reserved probe HTTP journal is missing.");
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct) ?? throw new InvalidOperationException("The captured probe transport failure is missing.");
        var configurationId = run["campaign_configuration"]?.ToString();
        var configuration = configurationId is { Length: 64 } ? await LoadAsync("planning-evaluation-configuration", "configuration:" + configurationId, ct) : null;
        var transport = journal["effective_transport_policy"] as JsonObject;
        var physical = journal["transport"] as JsonObject;
        if (await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null || journal["usage"] is not null ||
            logical.ClientRequestId != requestId || logical.Provider != run["provider"]?.ToString() || logical.Model != run["model"]?.ToString() ||
            logical.Tools is { Count: > 0 } || logical.UseBackgroundMode || logical.StructuredOutputSchema is null ||
            logical.MaxTokens is not (> 0 and <= 32768) || !logical.RequireOutputTokenLimit || !logical.DisableTransportRetries || logical.RequiredReviewCalls is not null ||
            independent["prompt_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(logical.Prompt) ||
            independent["schema_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(logical.StructuredOutputSchema.ToJsonString()) ||
            transport?["version"]?.GetValue<int>() != 2 || transport["mode"]?.ToString() != "runtime_single_attempt" ||
            transport["allow_http_recovery"]?.GetValue<bool>() != true || transport["disable_transport_retries"]?.GetValue<bool>() != true ||
            transport["request_hash"]?.ToString() != Hash(request) || journal["effective_transport_policy_hash"]?.ToString() != Hash(transport) ||
            transport["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1 || transport["effective_retry_policy"]?["MaxUncertainRetries"]?.GetValue<int>() != 0 ||
            physical?["Attempts"] is not JsonArray { Count: 1 } attempts || attempts[0] is not JsonObject attempt ||
            attempt["Id"]?.ToString() is not { Length: > 0 } attemptId || !attemptId.StartsWith(requestId + ":", StringComparison.Ordinal) ||
            attempt["StartedAtUtc"] is null || attempt["CompletedAtUtc"] is null || attempt["Status"] is not null ||
            attempt["Body"] is not null || attempt["AssembledBody"] is not null || attempt["ProtocolComplete"]?.GetValue<bool>() == true ||
            attempt["UsageKnown"]?.GetValue<bool>() == true || attempt["Failure"]?.ToString() is not ("transport" or "timeout") ||
            failure["stage"]?.ToString() != "dispatch" || failure["kind"]?.ToString() is not ("Transport" or "Timeout") ||
            failure["status_code"] is not null || failure["attempt_count"]?.GetValue<int>() != 1 ||
            journal["input_ceiling"]?.GetValue<long>() is not > 0 || journal["output_ceiling"]?.GetValue<long>() != logical.MaxTokens ||
            journal["cost_ceiling_eur"]?.GetValue<decimal>() is not > 0 || journal["exchange_quote"] is not JsonObject ||
            configuration?["configuration"] is not JsonObject frozen || frozen["provider"]?.ToString() != logical.Provider || frozen["model"]?.ToString() != logical.Model ||
            !(await CheckConfigurationSelectionAsync(configurationId!, frozen, configuration["host_configuration_hash"]!.ToString(), ct)).Accepted)
            throw new InvalidOperationException("Recoverable receipt/usage, changed ownership, policy, configuration or reservation excludes conservative probe retention.");
        // Both protected journal locations must remain absent: a probe creates no planning session.
        foreach (var owner in new[] { "agent-planning-model-requests-v11", "agent-planning-model-receipts-v11" })
            if (await Records.GetAsync(owner, "benchmark", runKey + ":" + requestId, Author, ct) is not null)
                throw new InvalidOperationException("Planning-owned evidence requires its existing session recovery path.");
        var body = physical["RequestBody"]?.GetValue<string>();
        var actual = body is null ? null : JsonNode.Parse(body) as JsonObject;
        if (physical["RequestMethod"]?.ToString() != "POST" || actual is null ||
            run["effective_http"]?["body_sha256"]?.ToString() != PlanningGraphCompiler.Fingerprint(body!) ||
            run["effective_http"]?["endpoint_sha256"]?.ToString() != PlanningGraphCompiler.Fingerprint(physical["Endpoint"]!.ToString()) ||
            actual["background"]?.GetValue<bool>() == true || actual["tools"] is JsonArray { Count: > 0 } tools &&
                tools.Any(t => t?["name"]?.ToString() != "gnougo_structured_output"))
            throw new InvalidOperationException("The exact tool-free foreground HTTP generation is not established.");
        var accounting = await BenchmarkHttpJournal.AccountingAsync(this, ct: ct);
        var evidence = new JsonObject { ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = collection,
            ["run_key"] = runKey, ["session_id"] = runKey, ["request_id"] = requestId, ["phase"] = "provider_probe", ["source"] = source,
            ["retention_kind"] = ProviderProbeRetention, ["run_hash"] = Hash(run), ["request_hash"] = Hash(request),
            ["http_journal_hash"] = Hash(journal), ["failure_hash"] = Hash(failure), ["configuration_hash"] = Hash(configuration),
            ["effective_transport_policy_hash"] = Hash(transport), ["accounting"] = accounting,
            ["request_accounting"] = LiveCampaignEvidence.Summarize([journal]),
            ["outcome_evidence"] = "recorded_transport_loss_without_completion_receipt",
            ["source_evidence"] = "server_provider_probe_and_frozen_configuration", ["no_business_effects"] = true,
            ["financial_admission_hash"] = Hash(new JsonObject { ["input_ceiling"] = journal["input_ceiling"]!.DeepClone(),
                ["output_ceiling"] = journal["output_ceiling"]!.DeepClone(), ["cost_ceiling_eur"] = journal["cost_ceiling_eur"]!.DeepClone(),
                ["exchange_quote"] = journal["exchange_quote"]!.DeepClone() }) };
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    private static bool ValidProviderProbeEvidence(JsonObject evidence) => evidence["run_collection"]?.ToString() == "planning-evaluation-runs" &&
        evidence["phase"]?.ToString() == "provider_probe" && evidence["session_id"]?.ToString() == evidence["run_key"]?.ToString() &&
        evidence["source_evidence"]?.ToString() == "server_provider_probe_and_frozen_configuration" &&
        evidence["outcome_evidence"]?.ToString() == "recorded_transport_loss_without_completion_receipt" && evidence["no_business_effects"]?.GetValue<bool>() == true &&
        evidence["configuration_hash"]?.ToString() is { Length: 64 } && evidence["failure_hash"]?.ToString() is { Length: 64 } &&
        evidence["request_accounting"] is JsonObject;
}
