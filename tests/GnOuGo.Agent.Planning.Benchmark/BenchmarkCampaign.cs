using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

/// <summary>Evaluation evidence uses the existing public encrypted record boundary, scoped to one campaign.</summary>
internal sealed class BenchmarkCampaign(IKeyVaultRecordStore records, string id)
{
    internal const string Author = "GnOuGo.Planning.Benchmark";
    internal string Id { get; } = ValidateId(id);
    internal IKeyVaultRecordStore Records => records;
    internal string? StopReason { get; private set; }
    private static string ValidateId(string value) => value.Length is >= 1 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        ? value : throw new ArgumentException("Invalid campaign identifier.");
    internal async Task<JsonObject?> LoadAsync(string collection, string key, CancellationToken ct = default)
        => await records.GetAsync(collection, "benchmark", Id + ":" + key, Author, ct) is { } record ? JsonNode.Parse(record.Value)!.AsObject() : null;
    internal Task SaveAsync(string collection, string key, JsonObject value, CancellationToken ct = default)
        => records.UpsertAsync(collection, "benchmark", Id + ":" + key, value.ToJsonString(), Author, ct);
    internal async Task PinAsync(JsonObject configuration, CancellationToken ct)
    {
        var saved = await LoadAsync("planning-evaluation-configuration", "configuration", ct);
        if (saved is not null && !JsonNode.DeepEquals(saved, configuration)) throw new InvalidOperationException("The campaign configuration changed; no model request was dispatched.");
        if (saved is null) await SaveAsync("planning-evaluation-configuration", "configuration", configuration, ct);
    }
    internal async Task<bool> HasUncertainRequestAsync(CancellationToken ct)
    {
        foreach (var request in (await records.ListAsync("planning-evaluation-requests", "benchmark", Author, ct)).Where(r => r.Key.StartsWith(Id + ":", StringComparison.Ordinal)))
            if (await records.GetAsync("planning-evaluation-receipts", "benchmark", request.Key, Author, ct) is null) return true;
        return false;
    }
    // Closing an exhausted evaluation is not a receipt: unknown usage stays fully reserved forever.
    // The caller holds the campaign's process lease. Original runs, requests and failures stay untouched.
    internal async Task<JsonObject> RetainInconclusiveAsync(string runKey, CancellationToken ct)
    {
        var run = await LoadAsync("planning-evaluation-runs", runKey, ct) ?? throw new InvalidOperationException("No recorded run.");
        var requestId = run["session"]?["pendingCall"]?["id"]?.ToString() ?? throw new InvalidOperationException("No uncertain request.");
        if (await LoadAsync("planning-evaluation-closures", requestId, ct) is { } saved) return saved;
        if (run["result"] is not JsonObject result || result["termination_reason"] is null || result["execution_correct"]?.GetValue<bool>() == true ||
            await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null ||
            await LoadAsync("planning-evaluation-failures", requestId, ct) is null ||
            await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct) is null)
            throw new InvalidOperationException("Only a saved failed run with uncertain HTTP evidence can be retained as inconclusive.");
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct) ?? throw new InvalidOperationException("No reserved request.");
        var accounting = await BenchmarkHttpJournal.AccountingAsync(this, requestId, ct: ct);
        if (accounting["session_calls"]!.GetValue<long>() < 8) throw new InvalidOperationException("The run still has an HTTP attempt allowance.");
        var closure = new JsonObject { ["run_key"] = runKey, ["request_id"] = requestId, ["reason"] = "session_http_attempts_exhausted",
            ["outcome"] = "inconclusive", ["retained_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["request_hash"] = PlanningGraphCompiler.Fingerprint(request.ToJsonString()), ["run_hash"] = PlanningGraphCompiler.Fingerprint(run.ToJsonString()),
            ["accounting_at_closure"] = accounting };
        await SaveAsync("planning-evaluation-closures", requestId, closure, ct);
        return closure;
    }
    // Read-only audit of a permanently closed admission denial. Zero attempts is
    // proof of no dispatch because the HTTP layer persists intent before sending.
    // A timeout, missing journal, changed record or any admitted attempt is ineligible.
    internal async Task<JsonObject?> AuditAdmissionDenialAsync(string runKey, JsonObject run, CancellationToken ct = default)
    {
        if (run["result"]?["termination_reason"]?.ToString() != "session_http_budget" ||
            run["result"]?["execution_correct"]?.GetValue<bool>() != false ||
            run["session"]?["pendingCall"]?["id"]?.ToString() is not { } requestId) return null;
        var closure = await LoadAsync("planning-evaluation-closures", requestId, ct);
        var request = await LoadAsync("planning-evaluation-requests", requestId, ct);
        var journal = await LoadAsync(BenchmarkHttpJournal.Collection, requestId, ct);
        var failure = await LoadAsync("planning-evaluation-failures", requestId, ct);
        if (closure?["reason"]?.ToString() != "session_http_attempts_exhausted" ||
            closure["run_key"]?.ToString() != runKey || request is null ||
            closure["run_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(run.ToJsonString()) ||
            closure["request_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(request.ToJsonString()) ||
            journal?["transport"]?["Attempts"] is not JsonArray { Count: 0 } || journal["usage"] is not null ||
            failure?["stage"]?.ToString() != "dispatch" ||
            run["usage_receipts"]?[requestId]?["transport_attempts"]?.GetValue<int>() != 0 ||
            await LoadAsync("planning-evaluation-receipts", requestId, ct) is not null) return null;
        var accounting = await BenchmarkHttpJournal.AccountingAsync(this, requestId, ct: ct);
        if (accounting["session_calls"]?.GetValue<long>() != 8 || closure["accounting_at_closure"]?["session_calls"]?.GetValue<long>() != 8) return null;
        var usage = run["usage_receipts"]!.AsObject();
        if (usage.Count != run["session"]?["modelCalls"]?.GetValue<int>() ||
            usage.Any(p => p.Value?["transport_attempts"]?.GetValue<int>() is not >= 0) ||
            usage.Sum(p => p.Value!["transport_attempts"]!.GetValue<int>()) != 8) return null;
        return new()
        {
            ["run_key"] = runKey, ["request_id"] = requestId, ["reason"] = "closed_request_never_admitted_to_http",
            ["original_run_hash"] = closure["run_hash"]!.DeepClone(), ["request_hash"] = closure["request_hash"]!.DeepClone(),
            ["http_journal_hash"] = PlanningGraphCompiler.Fingerprint(journal.ToJsonString()),
            ["failure_hash"] = PlanningGraphCompiler.Fingerprint(failure.ToJsonString()),
            ["closure_hash"] = PlanningGraphCompiler.Fingerprint(closure.ToJsonString()),
            ["admitted_request_attempts"] = 0, ["session_attempts"] = 8,
            ["original_result"] = run["result"]!.DeepClone()
        };
    }
    internal async Task<JsonObject> InspectAsync(CancellationToken ct = default)
    {
        var evidence = new List<KeyVaultRecordValue>();
        foreach (var collection in new[] { "requests", "receipts", "failures", "runs", "summaries", "configuration", "budgets", "http-attempts", "closures" })
            evidence.AddRange((await records.ListAsync("planning-evaluation-" + collection, "benchmark", Author, ct))
                .Where(r => collection == "budgets" ? r.Key == Id : r.Key.StartsWith(Id + ":", StringComparison.Ordinal)));
        var requests = evidence.Where(r => r.Collection == "planning-evaluation-requests").ToArray();
        var receipts = evidence.Where(r => r.Collection == "planning-evaluation-receipts").ToArray();
        var missing = requests.Where(r => !receipts.Any(receipt => receipt.Key == r.Key)).ToArray();
        var budget = evidence.SingleOrDefault(r => r.Collection == "planning-evaluation-budgets");
        var snapshot = budget is null ? null : JsonSerializer.Deserialize(budget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        return new() { ["campaign"] = Id, ["reserved_requests"] = requests.Length, ["completed_receipts"] = receipts.Length,
            ["uncertain_requests"] = new JsonArray(missing.Select(r => (JsonNode)JsonValue.Create(r.Key[(Id.Length + 1)..])).ToArray()),
            ["retained_inconclusive_requests"] = new JsonArray(evidence.Where(r => r.Collection == "planning-evaluation-closures").Select(r => (JsonNode)JsonValue.Create(r.Key[(Id.Length + 1)..])).ToArray()),
            ["transport_accounting"] = await BenchmarkHttpJournal.AccountingAsync(this, ct: ct),
            ["recorded_failures"] = evidence.Count(r => r.Collection == "planning-evaluation-failures"),
            ["known_budget_cost"] = snapshot?.EstimatedCost, ["budget_currency"] = snapshot?.EstimatedCostCurrency,
            ["known_input_tokens"] = snapshot?.InputTokens, ["known_output_tokens"] = snapshot?.OutputTokens,
            ["completion_receipts_complete"] = missing.Length == 0,
            ["evidence_hash"] = PlanningGraphCompiler.Fingerprint(string.Join('\n', evidence.OrderBy(r => r.Collection, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => new JsonArray(r.Collection, r.Key, r.Value).ToJsonString()))) };
    }
    internal async Task<(PlanningSession State, ILLMClient Client)> ReadReplayAsync(string runKey, CancellationToken ct = default)
    {
        var evidence = await LoadAsync("planning-evaluation-runs", runKey, ct) ?? throw new InvalidOperationException("No recorded run.");
        var saved = JsonSerializer.Deserialize(evidence["session"], PlanningJsonContext.Default.PlanningSession) ?? throw new InvalidOperationException("No recorded session.");
        if (saved.SchemaVersion != 10) throw new InvalidOperationException("Unsupported recorded session format.");
        var prefix = Id + ":" + saved.Request.SessionId + ":1:";
        var reservations = (await records.ListAsync("planning-evaluation-requests", "benchmark", Author, ct)).Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (reservations.Length != 1) throw new InvalidOperationException("Replay requires one original interpretation reservation.");
        var request = JsonSerializer.Deserialize(reservations[0].Value, PlanningJsonContext.Default.LLMRequest)!;
        if (request.ClientRequestId != reservations[0].Key[(Id.Length + 1)..]) throw new InvalidOperationException("The reservation identity does not match its stored request.");
        var receipt = await LoadAsync("planning-evaluation-receipts", request.ClientRequestId!, ct) ?? throw new InvalidOperationException("No completion receipt; replay cannot dispatch or invent a response.");
        var response = JsonSerializer.Deserialize(receipt, PlanningJsonContext.Default.LLMResponse)!;
        if (request.StructuredOutputSchema is null) throw new InvalidOperationException("The original response schema is missing.");
        // Only this detached in-memory session is bounded to one stored response.
        saved.Request.MaxModelCalls = 1; saved.Request.MaxReplanAttempts = 0;
        var state = new PlanningSession { Request = saved.Request, Catalog = saved.Catalog, Status = PlanningStatus.Generating,
            ModelCalls = 1, PendingCall = new() { Id = request.ClientRequestId!, Purpose = "intent", Request = request } };
        return (state, new ReplayReceipt(request, response));
    }

    private sealed class ReplayReceipt(LLMRequest original, LLMResponse response) : ILLMClient
    {
        private readonly string _request = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        private bool _used;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_used || JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest) != _request)
                throw new InvalidOperationException("Replay accepts the original reserved request exactly once; no live client exists.");
            _used = true;
            return Task.FromResult(JsonSerializer.Deserialize(JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), PlanningJsonContext.Default.LLMResponse)!);
        }
    }
    internal async Task<LLMResponse> CallAsync(LLMRequest request, Func<CancellationToken, Task> preflight, Func<CancellationToken, Task<LLMResponse>> dispatch, CancellationToken ct, bool allowHttpRecovery = false)
    {
        var key = request.ClientRequestId ?? throw new InvalidOperationException("A reserved request identity is required.");
        if (await LoadAsync("planning-evaluation-closures", key, ct) is not null) throw new InvalidOperationException("This request is permanently retained as inconclusive and cannot be dispatched again.");
        if (await LoadAsync("planning-evaluation-receipts", key, ct) is { } receipt)
            return JsonSerializer.Deserialize(receipt, PlanningJsonContext.Default.LLMResponse)!;
        var reserved = await LoadAsync("planning-evaluation-requests", key, ct);
        if (reserved is not null && !JsonNode.DeepEquals(reserved, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)))
            throw new InvalidOperationException("The reserved request changed.");
        var resumable = allowHttpRecovery && await LoadAsync(BenchmarkHttpJournal.Collection, key, ct) is not null;
        foreach (var pending in (await records.ListAsync("planning-evaluation-requests", "benchmark", Author, ct)).Where(r => r.Key.StartsWith(Id + ":", StringComparison.Ordinal)))
            if (await records.GetAsync("planning-evaluation-receipts", "benchmark", pending.Key, Author, ct) is null && await records.GetAsync("planning-evaluation-closures", "benchmark", pending.Key, Author, ct) is null && !(pending.Key == Id + ":" + key && resumable))
            { StopReason = "uncertain_dispatch"; throw new InvalidOperationException("The campaign has an uncertain dispatch without recoverable HTTP evidence."); }
        await preflight(ct);
        if (reserved is null) await SaveAsync("planning-evaluation-requests", key, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), ct);
        var stage = "dispatch";
        try
        {
            var response = await dispatch(ct);
            stage = "receipt_write";
            // Preserve evidence even when cancellation arrives after completion.
            await SaveAsync("planning-evaluation-receipts", key, JsonSerializer.SerializeToNode(response, PlanningJsonContext.Default.LLMResponse)!.AsObject(), CancellationToken.None);
            StopReason = null;
            return response;
        }
        catch (Exception ex)
        {
            StopReason ??= "uncertain_dispatch";
            var failure = ex as LLMClientException;
            var details = new JsonObject { ["stage"] = stage, ["exception_type"] = ex.GetType().Name, ["kind"] = failure?.Kind.ToString(),
                ["status_code"] = failure?.StatusCode, ["safe_provider_code"] = failure?.SafeProviderCode, ["retryable"] = failure?.Retryable,
                ["attempt_count"] = failure?.AttemptCount, ["retry_exhausted"] = failure?.RetryExhausted, ["retry_after_ms"] = failure?.RetryAfterMilliseconds };
            // A durable reservation already prevents redispatch. Failure evidence must not
            // replace the original exception if storage is itself unavailable.
            try
            {
                var previous = await LoadAsync("planning-evaluation-failures", key, CancellationToken.None);
                var history = previous?["previous_failures"]?.DeepClone().AsArray() ?? new JsonArray();
                previous?.Remove("previous_failures");
                if (previous is not null && !JsonNode.DeepEquals(previous, details)) history.Add(previous);
                if (history.Count > 0) details["previous_failures"] = history;
                await SaveAsync("planning-evaluation-failures", key, details, CancellationToken.None);
            }
            catch { }
            throw;
        }
    }
    internal void BudgetExceeded(bool session = false) => StopReason = session ? "session_http_budget" : "campaign_budget";
}
