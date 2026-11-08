using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

internal sealed partial class BenchmarkCampaign
{
    private sealed record ServerRetentionScope(EfPlanningSessionStore Store, IDbContextFactory<PlanningDbContext> Contexts,
        FileStream Lease, string Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Lease.DisposeAsync();
    }

    private async Task<ServerRetentionScope> OpenInterruptedServerAsync(JsonObject run, string collection, string runKey, string? workspace, CancellationToken ct)
    {
        if (collection != "planning-evaluation-runs" || run["run"]?.ToString() != runKey || run["campaign"]?.ToString() != Id ||
            runKey.Length is < 1 or > 70 || !runKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
            run["session_id"]?.ToString() is not { Length: > 0 and <= 128 } session ||
            !session.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || workspace is null)
            throw new InvalidOperationException("An exact owned Server run and workspace are required for interrupted inspection.");
        var database = Path.Combine(workspace, ".GnOuGo", "data", "live-server", runKey, "planning.db");
        if (!File.Exists(database)) throw new InvalidOperationException("The retained Server index is unavailable.");
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false };
        var contexts = new PooledDbContextFactory<PlanningDbContext>(new DbContextOptionsBuilder<PlanningDbContext>().UseSqlite(connection.ToString()).Options);
        var store = new EfPlanningSessionStore(contexts, Records);
        var lease = await store.TryAcquireLeaseAsync("benchmark", session, ct)
            ?? throw new InvalidOperationException("The Server session still has an active owner; retention cannot race it.");
        return new(store, contexts, lease, session);
    }

    private async Task<JsonObject> InspectInterruptedServerAsync(JsonObject run, string collection, string runKey, string requestId,
        ServerRetentionScope scope, CancellationToken ct, bool cancelled = false)
    {
        var state = await scope.Store.LoadAsync("benchmark", scope.Session, ct) ?? throw new InvalidOperationException("No exact Server session.");
        var pending = state.PendingCall;
        if (state.SchemaVersion != 11 || state.Request.TenantId != "benchmark" || state.Request.SessionId != scope.Session ||
            state.Status != (cancelled ? PlanningStatus.Cancelled : PlanningStatus.Generating) ||
            cancelled && run["result"]?["status"]?.ToString() != PlanningStatus.Cancelled ||
            state.Construction is null || pending is null || pending.Id != requestId ||
            pending.Request.ClientRequestId != requestId || !requestId.StartsWith(scope.Session + ":", StringComparison.Ordinal) ||
            pending.Purpose is not ("construction_intent" or "construction_select" or "construction_implementation" or "construction_discovery" or
                "construction_draft" or "construction_local_judge" or "construction_global_judge" or "replan") ||
            state.Phase != (pending.Purpose == "replan" ? PlanningPhase.Replanning : pending.Purpose) ||
            pending.Request.Tools is { Count: > 0 } || pending.Request.UseBackgroundMode || pending.Request.MaxTokens is not > 0 ||
            state.Yaml is not null || state.ApprovedHash is not null || state.ApprovalReview is not null || state.SavedAgentId is not null ||
            new[] { "artifact_hash", "execution_id", "execution_run_id", "execution_started", "execution", "final_review" }.Any(k => run[k] is not null) ||
            run["result"]?["execution_status"]?.ToString() != "not_started" || run["result"]?["execution_correct"]?.GetValue<bool>() != false ||
            run["provider"]?.ToString() != pending.Request.Provider || run["model"]?.ToString() != pending.Request.Model ||
            state.Request.Options["captured_model_metadata"]?["provider"]?.ToString() != pending.Request.Provider ||
            state.Request.Options["captured_model_metadata"]?["model"]?.ToString() != pending.Request.Model ||
            run["source"]?.ToString() is not { Length: 40 } source || !source.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Retention requires an interrupted tool-free Server generation with no approved or executed artifact.");

        var request = await LoadAsync("planning-evaluation-requests", requestId, ct);
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct);
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct);
        var policy = await LoadAsync("planning-evaluation-session-policy", scope.Session, ct);
        var retry = await LoadAsync("planning-evaluation-configuration", "http-retry-policy", ct);
        var key = scope.Session + ":" + requestId;
        async Task<JsonObject?> ServerRecord(string owner, string recordKey)
        {
            var record = await Records.GetAsync(owner, "benchmark", recordKey, Author, ct);
            return record is null ? null : JsonNode.Parse(record.Value)!.AsObject();
        }
        var serverRequest = await ServerRecord("agent-planning-model-requests-v11", key);
        var serverHttp = await ServerRecord("agent-planning-model-http-v11", requestId);
        var budget = await ServerRecord("agent-planning-budgets-v11", scope.Session);
        // Check both authoritative receipt locations before interpreting any empty
        // attempt list. A body/usage requires ordinary exact recovery instead.
        if (await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null ||
            await ServerRecord("agent-planning-model-receipts-v11", key) is not null || journal?["usage"] is not null || serverHttp?["transport"] is not null)
            throw new InvalidOperationException("Received evidence or a physical transport record requires exact recovery/reconciliation.");
        var exactRequest = JsonSerializer.SerializeToNode(pending.Request, PlanningJsonContext.Default.LLMRequest);
        var input = JsonSerializer.SerializeToUtf8Bytes(pending.Request, PlanningJsonContext.Default.LLMRequest).Length;
        var capture = PlanningTransportPolicies.Read(state.Request);
        var attemptKey = requestId + ":attempt:1";
        var applicationHold = budget?["journaledAttempts"]?[attemptKey];
        if (request is null || !JsonNode.DeepEquals(request, exactRequest) || !JsonNode.DeepEquals(serverRequest, exactRequest) ||
            policy is null || retry is null || capture is null ||
            journal?["transport"]?["Attempts"] is not JsonArray { Count: 0 } attempts ||
            journal["effective_transport_policy"] is not JsonObject effective || journal["effective_transport_policy_hash"]?.ToString() != Hash(effective) ||
            !MatchesInterruptedPlanning(state, policy, journal, attempts, allowEmptyTransport: true) ||
            !MatchesCapturedTokenAuthority(state.Request, journal["effective_transport_policy"] as JsonObject) ||
            policy["planning_format"]?.GetValue<int>() != 11 || policy["construction_review_headroom"]?.GetValue<int>() is not > 0 ||
            policy["max_physical_attempts"]?.GetValue<int>() != state.Request.MaxModelCalls ||
            policy["max_input_tokens"]?.GetValue<int>() != state.Request.Generation.MaxInputTokensPerRequest ||
            policy["max_output_tokens"]?.GetValue<int>() != state.Request.Generation.MaxOutputTokens ||
            policy["max_corrections"]?.GetValue<int>() != state.Request.MaxReplanAttempts ||
            !JsonNode.DeepEquals(policy["construction_limits"], state.Request.Options["construction_limits"]) ||
            !JsonNode.DeepEquals(policy[PlanningTokenThresholds.OptimizationOptionName], state.Request.Options[PlanningTokenThresholds.OptimizationOptionName]) ||
            !JsonNode.DeepEquals(journal["effective_transport_policy"]?[PlanningTokenThresholds.OptimizationOptionName], state.Request.Options[PlanningTokenThresholds.OptimizationOptionName]) ||
            journal["input_ceiling"]?.GetValue<long>() is not { } campaignInput || campaignInput < input ||
            journal["output_ceiling"]?.GetValue<int>() != pending.Request.MaxTokens || !ValidRetainedQuote(journal["exchange_quote"] as JsonObject) ||
            failure?["stage"]?.ToString() != "dispatch" || failure["exception_type"]?.ToString() is not ("TaskCanceledException" or "OperationCanceledException") ||
            new[] { "kind", "status_code", "safe_provider_code", "retryable", "attempt_count", "retry_exhausted", "retry_after_ms" }.Any(k => failure[k] is not null) ||
            serverHttp?["version"]?.GetValue<int>() != 1 || serverHttp["tenant"]?.ToString() != "benchmark" || serverHttp["session"]?.ToString() != scope.Session ||
            serverHttp["request_hash"]?.ToString() != Hash(request) || !JsonNode.DeepEquals(serverHttp["policy"], state.Request.Options[PlanningTransportPolicies.OptionName]) ||
            serverHttp["reserved_attempts"]?.GetValue<int>() != 1 || serverHttp["input_ceiling"]?.GetValue<int>() != input ||
            serverHttp["output_ceiling"]?.GetValue<int>() != pending.Request.MaxTokens ||
            budget?["uncertainCalls"]?.GetValue<int>() != 1 || applicationHold?["verifiedUsage"] is not null ||
            applicationHold?["inputTokenCeiling"]?.GetValue<int>() != input || applicationHold["outputTokenCeiling"]?.GetValue<int>() != pending.Request.MaxTokens ||
            applicationHold["reservedEstimatedCost"]?.GetValue<decimal>() is not > 0 || budget["estimatedCostCurrency"]?.ToString() != "EUR" ||
            journal["cost_ceiling_eur"]?.GetValue<decimal>() is not >= 0 ||
            journal["cost_ceiling_eur"]!.GetValue<decimal>() < applicationHold["reservedEstimatedCost"]!.GetValue<decimal>())
            throw new InvalidOperationException("The exact interrupted Server journals, cancellation failure and complete uncertain reservation do not match.");
        await using var db = await scope.Contexts.CreateDbContextAsync(ct);
        var index = await db.Calls.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == "benchmark" && c.SessionId == scope.Session && c.RequestHash == requestId, ct);
        if (index?.PayloadKey != key || index.Status != "reserved") throw new InvalidOperationException("The exact Server request is not reserved in its owned index.");
        var configurationId = run["campaign_configuration"]?.ToString();
        var amendment = configurationId is { Length: 64 } && configurationId.All(Uri.IsHexDigit)
            ? await LoadAsync("planning-evaluation-configuration", "configuration:" + configurationId, ct) : null;
        if (amendment?["configuration"] is not JsonObject configuration || configuration["provider"]?.ToString() != pending.Request.Provider ||
            configuration["model"]?.ToString() != pending.Request.Model)
            throw new InvalidOperationException("The interrupted Server deployment has no matching immutable campaign configuration.");
        var evidence = new JsonObject
        {
            ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = collection, ["run_key"] = runKey,
            ["session_id"] = scope.Session, ["revision"] = state.Revision, ["request_id"] = requestId, ["phase"] = pending.Purpose, ["source"] = source,
            ["run_hash"] = Hash(run), ["request_hash"] = Hash(request), ["http_journal_hash"] = Hash(journal),
            ["session_policy_hash"] = Hash(policy), ["campaign_retry_policy_hash"] = Hash(retry), ["configuration_hash"] = Hash(configuration),
            ["retention_kind"] = cancelled ? CancelledPlanningRetention : InterruptedProcessRetention, ["source_evidence"] = "server_session_and_frozen_configuration",
            ["outcome_evidence"] = cancelled ? "cancelled_reserved_attempt_without_persisted_outcome" : "reserved_attempt_without_persisted_outcome", ["dispatch_certainty"] = "unknown",
            ["server_session_hash"] = Hash(JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!.AsObject()),
            ["server_http_hash"] = Hash(serverHttp), ["server_budget_hash"] = Hash(budget), ["cancellation_failure_hash"] = Hash(failure),
            ["retained_application_reservation_eur"] = applicationHold["reservedEstimatedCost"]!.DeepClone(),
            ["application_reservation_in_campaign_http"] = false, ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, requestId, ct: ct)
        };
        if (cancelled) evidence["failure_hash"] = Hash(failure);
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    private static bool ValidInterruptedServerEvidence(JsonObject evidence)
        => new[] { "server_session_hash", "server_http_hash", "server_budget_hash", "cancellation_failure_hash" }
            .All(k => evidence[k]?.ToString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit)) &&
            evidence["retained_application_reservation_eur"]?.GetValue<decimal>() is > 0 &&
            evidence["application_reservation_in_campaign_http"]?.GetValue<bool>() == false && evidence["dispatch_certainty"]?.ToString() == "unknown";

    internal async Task RequireRetainedApplicationHoldsAsync(decimal supplied, CancellationToken ct, bool requireComplete = false)
    {
        var retained = await RetainedApplicationReservationsAsync(ct);
        if (supplied < retained) throw AdmissionFailure("retained_application_hold_understated");
        if (requireComplete && supplied != retained) throw AdmissionFailure("external_financial_history_incomplete");
    }

    // Share the exact verified, ordered conservative sum with read-only audits.
    internal async Task<decimal> RetainedApplicationReservationsAsync(CancellationToken ct)
    {
        decimal retained = 0;
        foreach (var row in await OwnedRecordsAsync("planning-evaluation-closures", ct))
        {
            var closure = JsonNode.Parse(row.Value)!.AsObject();
            var evidence = closure["evidence"] as JsonObject;
            if (closure["reason"]?.ToString() != PermanentRetentionReason || evidence?["source_evidence"]?.ToString() != "server_session_and_frozen_configuration" ||
                evidence["retention_kind"]?.ToString() is not (InterruptedProcessRetention or CancelledPlanningRetention) ||
                evidence["retention_kind"]?.ToString() == CancelledPlanningRetention && !evidence.ContainsKey("retained_application_reservation_eur")) continue;
            ValidatePermanentClosure(closure, row.Key[(Id.Length + 1)..]);
            var budget = await Records.GetAsync("agent-planning-budgets-v11", "benchmark", evidence["session_id"]!.ToString(), Author, ct);
            if (budget is null || Hash(JsonNode.Parse(budget.Value)!.AsObject()) != evidence["server_budget_hash"]!.ToString())
                throw AdmissionFailure("retained_application_hold_evidence_changed");
            retained = LLMUsageBudgetScope.SumCostConservatively(retained, evidence["retained_application_reservation_eur"]!.GetValue<decimal>());
        }
        return retained;
    }
}
