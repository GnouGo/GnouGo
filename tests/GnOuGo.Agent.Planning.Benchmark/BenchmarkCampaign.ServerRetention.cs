using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

internal sealed partial class BenchmarkCampaign
{
    private sealed record ServerRetentionScope(EfPlanningSessionStore Store, IDbContextFactory<PlanningDbContext> Contexts,
        FileStream Lease, string Session, string Directory) : IAsyncDisposable
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
        return new(store, contexts, lease, session, Path.GetDirectoryName(database)!);
    }

    private async Task<JsonObject> InspectInterruptedServerAsync(JsonObject run, string collection, string runKey, string requestId,
        ServerRetentionScope scope, CancellationToken ct, string retentionKind)
    {
        var state = await scope.Store.LoadAsync("benchmark", scope.Session, ct) ?? throw new InvalidOperationException("No exact Server session.");
        var pending = state.PendingCall;
        var cancelled = retentionKind is CancelledPlanningRetention or CancelledBeforeCampaignRetention;
        var refused = retentionKind == RefusedBeforeCampaignRetention;
        var storageFailure = retentionKind == StorageFailureBeforeCampaignRetention;
        var expectedStatus = refused || storageFailure ? PlanningStatus.Stopped : cancelled ? PlanningStatus.Cancelled : PlanningStatus.Generating;
        if (refused || storageFailure)
        {
            var matches = storageFailure
                ? state.Diagnostics is [{ Required: true, Code: "MODEL_DISPATCH_UNVERIFIABLE" }]
                : state.Diagnostics.Any(d => d.Required && d.Code is (ErrorCodes.LlmBudgetUnverifiable or ErrorCodes.LlmBudgetExceeded)
                    && d.BudgetFailure?.DispatchStatus == "not_started");
            if (!matches || run["session"]?["revision"]?.GetValue<int>() != state.Revision ||
                run["session"]?["pendingCall"]?["id"]?.ToString() != requestId ||
                !JsonNode.DeepEquals(run["session"]?["diagnostics"], JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)))
                throw new InvalidOperationException("The retained request requires the exact stopped Server diagnosis.");
        }
        if (state.SchemaVersion != 11 || state.Request.TenantId != "benchmark" || state.Request.SessionId != scope.Session ||
            state.Status != expectedStatus ||
            (cancelled || refused || storageFailure) && run["result"]?["status"]?.ToString() != expectedStatus ||
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
        if (retentionKind is CancelledBeforeCampaignRetention or RefusedBeforeCampaignRetention or StorageFailureBeforeCampaignRetention)
            return await InspectBeforeCampaignServerAsync(run, collection, runKey, requestId, scope, state, pending,
                serverRequest, serverHttp, budget, request, journal, failure, policy, retry, retentionKind, ct);
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
            budget?["uncertainCalls"]?.GetValue<int>() is not > 0 || applicationHold?["verifiedUsage"] is not null ||
            applicationHold?["inputTokenCeiling"]?.GetValue<int>() != input || applicationHold["outputTokenCeiling"]?.GetValue<int>() != pending.Request.MaxTokens ||
            applicationHold["reservedEstimatedCost"]?.GetValue<decimal>() is not > 0 || budget["estimatedCostCurrency"]?.ToString() != "EUR" ||
            journal["cost_ceiling_eur"]?.GetValue<decimal>() is not >= 0)
            throw new InvalidOperationException("The exact interrupted Server journals, cancellation failure and complete uncertain reservation do not match.");
        // These independently captured estimates can use different FX quotes.
        // With zero campaign attempts, retain the exact whole Server hold as an
        // external liability below; the campaign estimate does not cover it.
        var siblings = await InspectAccountedSiblingReservationsAsync(scope.Session, requestId, budget!, ct);
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
        if (siblings.Count > 0) evidence["accounted_sibling_reservations"] = siblings;
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    private async Task<JsonObject> InspectBeforeCampaignServerAsync(JsonObject run, string collection, string runKey, string requestId,
        ServerRetentionScope scope, PlanningSession state, PlanningModelCall pending, JsonObject? serverRequest, JsonObject? serverHttp,
        JsonObject? budget, JsonObject? request, JsonObject? journal, JsonObject? failure, JsonObject? policy, JsonObject? retry,
        string retentionKind, CancellationToken ct)
    {
        var exact = JsonSerializer.SerializeToNode(pending.Request, PlanningJsonContext.Default.LLMRequest)!;
        var input = JsonSerializer.SerializeToUtf8Bytes(pending.Request, PlanningJsonContext.Default.LLMRequest).Length;
        var hold = budget?["journaledAttempts"]?[requestId + ":attempt:1"];
        if (request is not null || journal is not null || failure is not null ||
            !JsonNode.DeepEquals(serverRequest, exact) || policy is null || retry is null || PlanningTransportPolicies.Read(state.Request) is null ||
            policy["planning_format"]?.GetValue<int>() != 11 || policy["construction_review_headroom"]?.GetValue<int>() is not > 0 ||
            policy["max_physical_attempts"]?.GetValue<int>() != state.Request.MaxModelCalls ||
            policy["max_input_tokens"]?.GetValue<int>() != state.Request.Generation.MaxInputTokensPerRequest ||
            policy["max_output_tokens"]?.GetValue<int>() != state.Request.Generation.MaxOutputTokens ||
            policy["max_corrections"]?.GetValue<int>() != state.Request.MaxReplanAttempts ||
            !JsonNode.DeepEquals(policy["construction_limits"], state.Request.Options["construction_limits"]) ||
            !JsonNode.DeepEquals(policy[PlanningTokenThresholds.OptionName], state.Request.Options[PlanningTokenThresholds.OptionName]) ||
            !JsonNode.DeepEquals(policy[PlanningTokenThresholds.OptimizationOptionName], state.Request.Options[PlanningTokenThresholds.OptimizationOptionName]) ||
            serverHttp?["version"]?.GetValue<int>() != 1 || serverHttp["tenant"]?.ToString() != "benchmark" ||
            serverHttp["session"]?.ToString() != scope.Session || serverHttp["request_hash"]?.ToString() != Hash(serverRequest!) ||
            !JsonNode.DeepEquals(serverHttp["policy"], state.Request.Options[PlanningTransportPolicies.OptionName]) ||
            serverHttp["reserved_attempts"]?.GetValue<int>() != 1 || serverHttp["input_ceiling"]?.GetValue<int>() != input ||
            serverHttp["output_ceiling"]?.GetValue<int>() != pending.Request.MaxTokens || serverHttp["transport"] is not null ||
            budget?["uncertainCalls"]?.GetValue<int>() is not > 0 || budget["estimatedCostCurrency"]?.ToString() != "EUR" ||
            hold?["verifiedUsage"] is not null || hold?["inputTokenCeiling"]?.GetValue<int>() != input ||
            hold["outputTokenCeiling"]?.GetValue<int>() != pending.Request.MaxTokens || hold["reservedEstimatedCost"]?.GetValue<decimal>() is not > 0)
            throw new InvalidOperationException("Pre-campaign retention requires absent campaign records and exact owned Server reservation evidence.");
        var siblings = await InspectAccountedSiblingReservationsAsync(scope.Session, requestId, budget!, ct);
        await using var db = await scope.Contexts.CreateDbContextAsync(ct);
        var index = await db.Calls.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == "benchmark" && c.SessionId == scope.Session && c.RequestHash == requestId, ct);
        if (index?.PayloadKey != scope.Session + ":" + requestId || index.Status != "reserved")
            throw new InvalidOperationException("The exact Server request is not reserved in its owned index.");
        var configurationId = run["campaign_configuration"]?.ToString();
        var amendment = configurationId is { Length: 64 } && configurationId.All(Uri.IsHexDigit)
            ? await LoadAsync("planning-evaluation-configuration", "configuration:" + configurationId, ct) : null;
        if (amendment?["configuration"] is not JsonObject configuration || configuration["provider"]?.ToString() != pending.Request.Provider ||
            configuration["model"]?.ToString() != pending.Request.Model)
            throw new InvalidOperationException("The Server deployment has no matching immutable campaign configuration.");
        var evidence = new JsonObject
        {
            ["version"] = 1, ["campaign"] = Id, ["tenant"] = "benchmark", ["run_collection"] = collection, ["run_key"] = runKey,
            ["session_id"] = scope.Session, ["revision"] = state.Revision, ["request_id"] = requestId, ["phase"] = pending.Purpose, ["source"] = run["source"]!.DeepClone(),
            ["run_hash"] = Hash(run), ["request_hash"] = Hash(serverRequest!), ["session_policy_hash"] = Hash(policy),
            ["campaign_retry_policy_hash"] = Hash(retry), ["configuration_hash"] = Hash(configuration), ["retention_kind"] = retentionKind,
            ["source_evidence"] = "server_session_and_frozen_configuration", ["outcome_evidence"] = BeforeCampaignOutcome(retentionKind),
            ["dispatch_certainty"] = "unknown", ["campaign_envelope_absent"] = true,
            ["server_session_hash"] = Hash(JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!.AsObject()),
            ["server_http_hash"] = Hash(serverHttp), ["server_budget_hash"] = Hash(budget),
            ["retained_application_reservation_eur"] = hold["reservedEstimatedCost"]!.DeepClone(), ["application_reservation_in_campaign_http"] = false,
            ["accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, requestId, ct: ct)
        };
        if (retentionKind == StorageFailureBeforeCampaignRetention)
            evidence["storage_failure_span_hash"] = await StorageFailureSpanHashAsync(scope, requestId, ct);
        if (siblings.Count > 0) evidence["accounted_sibling_reservations"] = siblings;
        return new() { ["evidence"] = evidence, ["evidence_hash"] = Hash(evidence), ["outcome"] = "inconclusive",
            ["reservation_retained"] = true, ["request_replay_forbidden"] = true, ["run_continuation_forbidden"] = true };
    }

    private static string? BeforeCampaignOutcome(string? kind) => kind switch
    {
        RefusedBeforeCampaignRetention => "admission_refused_with_server_reservation_before_campaign_envelope",
        CancelledBeforeCampaignRetention => "cancelled_with_server_reservation_before_campaign_envelope",
        StorageFailureBeforeCampaignRetention => "storage_failure_with_server_reservation_before_campaign_envelope",
        _ => null
    };

    private static async Task<string> StorageFailureSpanHashAsync(ServerRetentionScope scope, string request, CancellationToken ct)
    {
        var path = Path.Combine(scope.Directory, "telemetry.db");
        var options = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false
        };
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(options.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Match the entire logical dispatch inventory before accepting its single failed observation.
        command.CommandText = """
            SELECT hex(trace_id), hex(span_id), start_unix_ns, end_unix_ns, status_code, attributes_json
            FROM span_records WHERE name = 'gen_ai.generate'
                AND json_extract(attributes_json, '$."gnougo.llm.request_id"') = $request
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$request", request);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The original model dispatch trace is missing.");
        var attributes = JsonNode.Parse(reader.GetString(5));
        if (reader.GetString(0).Length != 32 || reader.GetString(1).Length != 16 ||
            reader.GetInt64(2) <= 0 || reader.GetInt64(3) < reader.GetInt64(2) || reader.GetInt32(4) != 2 ||
            attributes?["tenant.id"]?.ToString() != "benchmark" || attributes["gnougo.planning.session_id"]?.ToString() != scope.Session ||
            attributes["gnougo.llm.status"]?.ToString() != "failed" || attributes["error.type"]?.ToString() != nameof(KeyVaultAccessException))
            throw new InvalidOperationException("The owned model trace does not prove a storage failure.");
        var hash = Hash(new JsonObject { ["trace"] = reader.GetString(0), ["span"] = reader.GetString(1),
            ["start"] = reader.GetInt64(2), ["end"] = reader.GetInt64(3), ["attributes"] = attributes });
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException("The model dispatch trace is ambiguous.");
        return hash;
    }

    private async Task<JsonArray> InspectAccountedSiblingReservationsAsync(string session, string pendingId, JsonObject budget, CancellationToken ct)
    {
        if (budget["journaledAttempts"] is not JsonObject attempts)
            throw new InvalidOperationException("The Server reservation journal is unavailable.");
        var unknown = attempts.Where(pair => pair.Value?["verifiedUsage"] is null).ToArray();
        if (budget["uncertainCalls"]?.GetValue<int>() != unknown.Length ||
            unknown.Count(pair => pair.Key == pendingId + ":attempt:1") != 1)
            throw new InvalidOperationException("The complete Server uncertain-attempt count does not match its journal.");
        var result = new JsonArray();
        foreach (var (key, hold) in unknown.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (key == pendingId + ":attempt:1") continue;
            var marker = key.LastIndexOf(":attempt:", StringComparison.Ordinal);
            if (marker <= 0 || !int.TryParse(key.AsSpan(marker + 9), out var number) || number < 1 ||
                key != key[..marker] + ":attempt:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("An additional Server reservation has no exact attempt identity.");
            var id = key[..marker];
            if (id == pendingId || !id.StartsWith(session + ":", StringComparison.Ordinal))
                throw new InvalidOperationException("An additional reservation belongs to another or still-pending request.");
            var request = await LoadAsync("planning-evaluation-requests", id, ct);
            var journal = await LoadAsync(BenchmarkHttpJournal.Collection, id, ct);
            var receipt = await LoadAsync("planning-evaluation-receipts", id, ct);
            var serverRequest = await Records.GetAsync("agent-planning-model-requests-v11", "benchmark", session + ":" + id, Author, ct);
            var serverReceipt = await Records.GetAsync("agent-planning-model-receipts-v11", "benchmark", session + ":" + id, Author, ct);
            if (request is null || journal is null || receipt is null || hold is not JsonObject reservation ||
                request["clientRequestId"]?.ToString() != id || request["tools"] is JsonArray { Count: > 0 } ||
                serverRequest is null || !JsonNode.DeepEquals(request, JsonNode.Parse(serverRequest.Value)) || serverReceipt is null ||
                journal["transport"]?["Attempts"] is not JsonArray physical || physical.Count < number ||
                receipt["usage"] is not JsonObject usage || !JsonNode.DeepEquals(usage, journal["usage"]) ||
                usage["benchmark_cost_eur"]?.GetValue<decimal>() is not >= 0)
                throw new InvalidOperationException("An additional Server reservation has no exact completed campaign request.");
            var amount = reservation["reservedEstimatedCost"]?.GetValue<decimal>() ?? throw new InvalidOperationException("Missing application hold.");
            var heldInput = reservation["inputTokenCeiling"]?.GetValue<long>() ?? throw new InvalidOperationException("Missing input hold.");
            var heldOutput = reservation["outputTokenCeiling"]?.GetValue<int>() ?? throw new InvalidOperationException("Missing output hold.");
            var ceiling = journal["cost_ceiling_eur"]?.GetValue<decimal>() ?? throw new InvalidOperationException("Missing campaign hold.");
            var input = journal["input_ceiling"]?.GetValue<long>() ?? throw new InvalidOperationException("Missing campaign input bound.");
            var output = journal["output_ceiling"]?.GetValue<int>() ?? throw new InvalidOperationException("Missing campaign output bound.");
            if (amount <= 0 || heldInput <= 0 || heldOutput <= 0 || ceiling < amount || input < heldInput || output < heldOutput ||
                !PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(physical[number - 1]?["Status"]?.GetValue<int>(), true,
                    PlanningBenchmarkMeasurements.RetainsAllHttpFailures(journal)))
                throw new InvalidOperationException("An additional Server reservation is not fully retained by its exact completed campaign request.");
            result.Add(new JsonObject { ["application_attempt_id"] = key, ["http_journal_hash"] = Hash(journal),
                ["request_hash"] = Hash(request), ["receipt_hash"] = Hash(receipt), ["server_receipt_hash"] = Hash(JsonNode.Parse(serverReceipt.Value)!.AsObject()),
                ["application_reservation_eur"] = amount, ["campaign_attempt_ceiling_eur"] = ceiling });
        }
        return result;
    }

    private static bool ValidBeforeCampaignServerEvidence(JsonObject evidence)
        => evidence["run_collection"]?.ToString() == "planning-evaluation-runs" &&
            evidence["source_evidence"]?.ToString() == "server_session_and_frozen_configuration" &&
            evidence["outcome_evidence"]?.ToString() == BeforeCampaignOutcome(evidence["retention_kind"]?.ToString()) &&
            (evidence["retention_kind"]?.ToString() != StorageFailureBeforeCampaignRetention ||
                evidence["storage_failure_span_hash"]?.ToString() is { Length: 64 } span && span.All(Uri.IsHexDigit)) &&
            evidence["campaign_envelope_absent"]?.GetValue<bool>() == true &&
            new[] { "server_session_hash", "server_http_hash", "server_budget_hash", "request_hash", "configuration_hash", "session_policy_hash", "campaign_retry_policy_hash" }
                .All(k => evidence[k]?.ToString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit)) &&
            new[] { "http_journal_hash", "failure_hash", "cancellation_failure_hash" }.All(k => !evidence.ContainsKey(k)) &&
            evidence["retained_application_reservation_eur"]?.GetValue<decimal>() is > 0 &&
            evidence["application_reservation_in_campaign_http"]?.GetValue<bool>() == false && evidence["dispatch_certainty"]?.ToString() == "unknown";

    private static bool ValidInterruptedServerEvidence(JsonObject evidence)
        => new[] { "server_session_hash", "server_http_hash", "server_budget_hash", "cancellation_failure_hash" }
            .All(k => evidence[k]?.ToString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit)) &&
            evidence["retained_application_reservation_eur"]?.GetValue<decimal>() is > 0 &&
            evidence["application_reservation_in_campaign_http"]?.GetValue<bool>() == false && evidence["dispatch_certainty"]?.ToString() == "unknown";

    internal async Task RequireRetainedApplicationHoldsAsync(decimal supplied, CancellationToken ct, bool requireComplete = false,
        PlanningSession? activeState = null, LLMRequest? activeRequest = null)
    {
        var retained = await RetainedApplicationReservationsAsync(ct, activeState, activeRequest);
        if (supplied < retained) throw AdmissionFailure("retained_application_hold_understated");
        if (requireComplete && supplied != retained) throw AdmissionFailure("external_financial_history_incomplete");
    }

    private async Task RequireNoUnretainedPreCampaignHoldsAsync(CancellationToken ct, PlanningSession? activeState, LLMRequest? activeRequest)
    {
        // The Server already owns this exact active dispatch. Its reservation is
        // covered by normal current-request physical admission, not external history.
        var current = activeState is { Status: PlanningStatus.Generating, PendingCall: { } pending } &&
            activeState.Request.TenantId == "benchmark" && activeRequest?.ClientRequestId == pending.Id &&
            JsonNode.DeepEquals(JsonSerializer.SerializeToNode(activeRequest, PlanningJsonContext.Default.LLMRequest),
                JsonSerializer.SerializeToNode(pending.Request, PlanningJsonContext.Default.LLMRequest)) ? pending.Id : null;
        // Session-policy keys are existing campaign-owned associations. Read only
        // their bounded budget metadata, never unrelated sessions or request bodies.
        foreach (var row in await OwnedRecordsAsync("planning-evaluation-session-policy", ct))
        {
            var session = row.Key[(Id.Length + 1)..];
            var record = await Records.GetAsync("agent-planning-budgets-v11", "benchmark", session, Author, ct);
            if (record is null) continue;
            var budget = JsonNode.Parse(record.Value)!;
            if (budget["uncertainCalls"]?.GetValue<int>() is > 0 && budget["journaledAttempts"] is not JsonObject)
                throw AdmissionFailure("server_application_hold_identity_unverifiable");
            foreach (var (attempt, value) in budget["journaledAttempts"] as JsonObject ?? [])
            {
                if (value?["verifiedUsage"] is not null) continue;
                var split = attempt.LastIndexOf(":attempt:", StringComparison.Ordinal);
                if (split <= session.Length || !attempt.StartsWith(session + ":", StringComparison.Ordinal))
                    throw AdmissionFailure("server_application_hold_identity_unverifiable");
                var request = attempt[..split];
                if (request == current) continue;
                if (await LoadAsync("planning-evaluation-requests", request, ct) is not null ||
                    await LoadAsync(BenchmarkHttpJournal.Collection, request, ct) is not null) continue;
                var closure = await LoadAsync("planning-evaluation-closures", request, ct);
                if (closure is null || closure["reason"]?.ToString() != PermanentRetentionReason)
                    throw AdmissionFailure("unretained_server_application_hold_before_campaign_envelope");
                ValidatePermanentClosure(closure, request);
            }
        }
    }

    // Share the exact verified, ordered conservative sum with read-only audits.
    internal async Task<decimal> RetainedApplicationReservationsAsync(CancellationToken ct, PlanningSession? activeState = null, LLMRequest? activeRequest = null)
    {
        await RequireNoUnretainedPreCampaignHoldsAsync(ct, activeState, activeRequest);
        decimal retained = 0;
        foreach (var row in await OwnedRecordsAsync("planning-evaluation-closures", ct))
        {
            var closure = JsonNode.Parse(row.Value)!.AsObject();
            var evidence = closure["evidence"] as JsonObject;
            if (closure["reason"]?.ToString() != PermanentRetentionReason || evidence is null) continue;
            var request = row.Key[(Id.Length + 1)..];
            if (evidence["retention_kind"]?.ToString() == UnstartedPlanningRetention && evidence["run_collection"]?.ToString() == "planning-evaluation-runs")
            {
                ValidatePermanentClosure(closure, request);
                retained = LLMUsageBudgetScope.SumCostConservatively(retained, await UnstartedServerReservationAsync(evidence, request, ct));
                continue;
            }
            if (evidence["source_evidence"]?.ToString() != "server_session_and_frozen_configuration" ||
                evidence["retention_kind"]?.ToString() is not (InterruptedProcessRetention or CancelledPlanningRetention or CancelledBeforeCampaignRetention or RefusedBeforeCampaignRetention or StorageFailureBeforeCampaignRetention) ||
                evidence["retention_kind"]?.ToString() == CancelledPlanningRetention && !evidence.ContainsKey("retained_application_reservation_eur")) continue;
            ValidatePermanentClosure(closure, request);
            var budget = await Records.GetAsync("agent-planning-budgets-v11", "benchmark", evidence["session_id"]!.ToString(), Author, ct);
            if (budget is null || Hash(JsonNode.Parse(budget.Value)!.AsObject()) != evidence["server_budget_hash"]!.ToString())
                throw AdmissionFailure("retained_application_hold_evidence_changed");
            retained = LLMUsageBudgetScope.SumCostConservatively(retained, evidence["retained_application_reservation_eur"]!.GetValue<decimal>());
        }
        return retained;
    }

    private async Task<decimal> UnstartedServerReservationAsync(JsonObject evidence, string request, CancellationToken ct)
    {
        var run = await LoadAsync("planning-evaluation-runs", evidence["run_key"]!.ToString(), ct);
        if (run is null || Hash(run) != evidence["run_hash"]?.ToString())
            throw AdmissionFailure("retained_application_hold_evidence_changed");
        if (run["host"]?.ToString() != "GnOuGo.Agent.Server") return 0;
        var session = evidence["session_id"]!.ToString();
        var budget = await Records.GetAsync("agent-planning-budgets-v11", "benchmark", session, Author, ct);
        var current = budget is null ? null : JsonNode.Parse(budget.Value);
        var snapshot = run["session"]?["usage"];
        var original = RequestAttempts(snapshot);
        if (run["session_id"]?.ToString() != session || !JsonNode.DeepEquals(original, RequestAttempts(current)))
            throw AdmissionFailure("retained_application_hold_evidence_changed");
        if (original.Count == 0) return 0;
        if (snapshot?["estimatedCostCurrency"]?.ToString() != "EUR" || current?["estimatedCostCurrency"]?.ToString() != "EUR")
            throw AdmissionFailure("unverifiable_server_application_reservation");
        decimal total = 0;
        foreach (var (_, hold) in original.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (hold?["verifiedUsage"] is not null || hold?["reservedEstimatedCost"]?.GetValue<decimal>() is not (>= 0 and var amount))
                throw AdmissionFailure("unverifiable_server_application_reservation");
            total = LLMUsageBudgetScope.SumCostConservatively(total, amount);
        }
        return total;

        JsonObject RequestAttempts(JsonNode? usage) => new((usage?["journaledAttempts"] as JsonObject ?? [])
            .Where(pair => pair.Key.StartsWith(request + ":attempt:", StringComparison.Ordinal))
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
    }
}
