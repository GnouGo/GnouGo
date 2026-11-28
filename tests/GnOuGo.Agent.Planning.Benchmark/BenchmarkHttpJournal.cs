using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

/// <summary>Admission and encrypted evidence only. All retry decisions belong to AI.Core.</summary>
internal sealed class BenchmarkHttpJournal(BenchmarkCampaign campaign, string requestId, long inputCeiling, long outputCeiling, decimal costCeiling,
    int? sessionAttemptLimit = 8, decimal reviewCostHeadroom = 0, int reviewAttemptHeadroom = 0,
    JsonObject? effectiveTransportPolicy = null, TimeProvider? time = null) : ILLMHttpRetryJournal
{
    private readonly JsonObject? _effectiveTransportPolicy = effectiveTransportPolicy?.DeepClone().AsObject();
    internal const string Collection = "planning-evaluation-http-attempts";
    internal static bool IsRuntimeSingleAttempt(JsonObject? policy) => policy?["version"]?.GetValue<int>() == 2 &&
        policy["mode"]?.ToString() is "runtime_single_attempt" or "sdk_proxy_single_attempt";

    internal static bool IsRuntimePolicy(JsonObject? policy) => IsRuntimeSingleAttempt(policy) ||
        policy?["version"]?.GetValue<int>() == 2 && policy["mode"]?.ToString() is "runtime_confirmed_retry" or "sdk_proxy_confirmed_retry";

    internal static async Task<JsonObject> InspectRequestAsync(BenchmarkCampaign campaign, string requestId, CancellationToken ct)
    {
        var stored = await campaign.ReadRecordAsync(Collection, requestId, ct) ?? throw new InvalidOperationException("No HTTP journal for this campaign request.");
        var record = JsonNode.Parse(stored.Value)!.AsObject();
        if (record["transport"] is not JsonObject wire || wire["Attempts"] is not JsonArray)
            throw new InvalidDataException("The retained HTTP journal has no attempt inventory.");
        var accounting = LiveCampaignEvidence.Summarize([record]);
        var body = wire["RequestBody"]?.GetValue<string>();
        return new()
        {
            ["campaign"] = campaign.Id, ["request_id"] = requestId, ["wire_sha256"] = Digest(wire["Fingerprint"]),
            ["journal_created_utc"] = stored.CreatedAt.ToUniversalTime(), ["journal_updated_utc"] = stored.UpdatedAt.ToUniversalTime(),
            ["request_body_utf8_bytes"] = body is null ? null : System.Text.Encoding.UTF8.GetByteCount(body),
            ["request_body_sha256"] = body is null ? null : PlanningGraphCompiler.Fingerprint(body),
            ["attempts"] = AttemptEvidence(wire), ["unknown_usage_attempts"] = accounting["unknown_attempts"]?.DeepClone(),
            ["reserved_cost_eur"] = accounting["reserved_cost_eur"]?.DeepClone(),
            ["measured_cost_eur"] = record["usage"] is null ? null : accounting["known_cost_eur"]?.DeepClone()
        };
    }

    // Read only the metadata fields. Provider bodies, tool arguments and opaque receipt IDs stay in the vault.
    internal static JsonArray? AttemptEvidence(JsonObject wire)
    {
        if (wire["Attempts"] is not JsonArray attempts) return null;
        var evidence = new JsonArray();
        foreach (var attempt in attempts)
        {
            var row = attempt!.AsObject();
            var summary = TransportEvent(row, "Id");
            summary["observed_status"] = row["ObservedStatus"]?.GetValue<int>();
            summary["protocol_complete"] = row["ProtocolComplete"]?.GetValue<bool>();
            summary["usage_known"] = row["UsageKnown"]?.GetValue<bool>();
            summary["response_id_available"] = row["ProviderResponseId"] is not null;
            summary["body_available"] = row["Body"] is not null || row["AssembledBody"] is not null;
            summary["partial_body_available"] = row["PartialBody"] is not null || row["PartialBodyBase64"] is not null;
            summary["receipt_retrievals"] = new JsonArray((row["ReceiptRetrievals"] as JsonArray ?? []).Select(poll =>
                (JsonNode)TransportEvent(poll!.AsObject(), "RequestId")).ToArray());
            evidence.Add(summary);
        }
        return evidence;
    }

    private static JsonObject TransportEvent(JsonObject row, string idKey)
    {
        var timing = row["ProviderRetryDelay"]?.Deserialize(LLMHttpRetryJsonContext.Default.ProviderRetryDelay);
        if (timing is null && row["RetryAfter"]?.GetValue<string>() is { } legacy && row["CompletedAtUtc"]?.GetValue<DateTimeOffset>() is { } captured)
        {
            using var response = new HttpResponseMessage();
            response.Headers.TryAddWithoutValidation("Retry-After", legacy);
            timing = ProviderRetryDelay.Read(response.Headers, captured);
        }
        var phase = row["FailurePhase"]?.GetValue<string>();
        return new()
        {
            ["physical_id_sha256"] = row[idKey]?.GetValue<string>() is { Length: > 0 } id ? PlanningGraphCompiler.Fingerprint(id) : null,
            ["provider_request_id_sha256"] = Digest(row["ProviderRequestIdHash"]),
            ["status"] = row["Status"]?.GetValue<int>(), ["started_utc"] = row["StartedAtUtc"]?.GetValue<DateTimeOffset>(),
            ["completed_utc"] = row["CompletedAtUtc"]?.GetValue<DateTimeOffset>(),
            ["failure_phase"] = phase is "response_headers" or "response_body" or "provider_rejection" or "receipt_retrieval" ? phase : null,
            ["retry_header"] = timing?.Header is "Retry-After" or "retry-after-ms" or "x-ms-retry-after-ms" ? timing.Header : null,
            ["requested_delay_ms"] = timing?.Remaining(timing.ObservedAtUtc).TotalMilliseconds,
            ["retry_not_before_utc"] = row["NotBefore"]?.GetValue<DateTimeOffset>() ?? timing?.NotBeforeUtc
        };
    }

    private static string? Digest(JsonNode? node)
        => node?.GetValue<string>() is { Length: 64 } digest && digest.All(char.IsAsciiHexDigit) ? digest.ToLowerInvariant() : null;

    internal static JsonObject UndispatchedUsage() => new()
    {
        ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["benchmark_cost_eur"] = 0m,
        ["transport_attempts"] = 0, ["uncertain_attempts"] = 0, ["reserved_input_tokens"] = 0L,
        ["reserved_output_tokens"] = 0L, ["reserved_cost_eur"] = 0m, ["benchmark_usage_bounded"] = true
    };
    internal async Task PrepareAsync(CancellationToken ct, JsonObject? exchangeQuote = null)
    {
        if (await LoadAsync(ct) is null) await SaveAsync(new(), ct, exchangeQuote);
    }
    public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct)
        => (await campaign.LoadAsync(Collection, requestId, ct))?["transport"] is { } state
            ? JsonSerializer.Deserialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState) : null;

    public Task SaveAsync(LLMHttpRetryState state, CancellationToken ct) => SaveAsync(state, ct, null);

    private async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct, JsonObject? exchangeQuote)
    {
        ct.ThrowIfCancellationRequested();
        var existing = await campaign.LoadAsync(Collection, requestId, ct);
        if (existing is not null && !JsonNode.DeepEquals(existing["effective_transport_policy"], _effectiveTransportPolicy))
            throw new InvalidOperationException("An issued effective transport policy cannot change during recovery.");
        if (existing?["effective_transport_policy"] is JsonObject captured &&
            existing["effective_transport_policy_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(captured.ToJsonString()))
            throw new InvalidOperationException("The captured effective transport policy hash changed.");
        if (sessionAttemptLimit is <= 0 || existing is not null &&
            (existing.ContainsKey("session_attempt_limit") ? existing["session_attempt_limit"]?.GetValue<int>() : 8) != sessionAttemptLimit)
            throw new InvalidOperationException("An issued HTTP attempt limit cannot change during recovery.");
        var maximumReviews = 2;
        if (reviewAttemptHeadroom > 2 && sessionAttemptLimit is { } capturedLimit &&
            _effectiveTransportPolicy?["version"]?.GetValue<int>() == 2 && !IsRuntimePolicy(_effectiveTransportPolicy))
        {
            var planning = await campaign.LoadAsync("planning-evaluation-session-policy", requestId.Split(':')[0], ct);
            if (planning?["planning_format"]?.GetValue<int>() == 11 &&
                planning["max_physical_attempts"]?.GetValue<int>() == capturedLimit &&
                planning[PlanningTransportPolicies.OptionName] is JsonObject transport &&
                JsonNode.DeepEquals(transport, _effectiveTransportPolicy["planning_transport_policy"]))
                maximumReviews = PlanningTokenThresholds.OptimizationAdvisory(planning) &&
                    PlanningTokenThresholds.OptimizationAdvisory(_effectiveTransportPolicy) ? reviewAttemptHeadroom : capturedLimit;
        }
        if (reviewCostHeadroom < 0 || reviewAttemptHeadroom < 0 || reviewAttemptHeadroom > maximumReviews || existing is not null &&
            ((existing["construction_review_cost_headroom_eur"]?.GetValue<decimal>() ?? 0) != reviewCostHeadroom ||
             (existing["construction_review_attempt_headroom"]?.GetValue<int>() ?? 0) != reviewAttemptHeadroom))
            throw new InvalidOperationException("Issued construction review headroom cannot change during recovery.");
        var record = existing?.DeepClone().AsObject() ?? new JsonObject
        {
            ["input_ceiling"] = inputCeiling, ["output_ceiling"] = outputCeiling, ["cost_ceiling_eur"] = costCeiling, ["session_attempt_limit"] = sessionAttemptLimit
        };
        if (existing is null && reviewAttemptHeadroom > 0)
        {
            record["construction_review_cost_headroom_eur"] = reviewCostHeadroom;
            record["construction_review_attempt_headroom"] = reviewAttemptHeadroom;
        }
        if (inputCeiling <= 0 || outputCeiling <= 0 || costCeiling < 0) throw new InvalidOperationException("Conservative attempt limits are required.");
        if (existing is null && exchangeQuote is not null) record["exchange_quote"] = exchangeQuote.DeepClone();
        if (existing is null && _effectiveTransportPolicy is not null)
        {
            record["effective_transport_policy"] = _effectiveTransportPolicy.DeepClone();
            record["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(_effectiveTransportPolicy.ToJsonString());
        }
        record["transport"] = JsonSerializer.SerializeToNode(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        var oldCount = existing?["transport"]?["Attempts"]?.AsArray().Count ?? 0;
        if (state.Attempts.Count > oldCount)
        {
            await campaign.RequireOpenSpendingAsync(ct);
            _ = await campaign.FreshAllocationAsync(ct);
            if (state.Attempts.Count != oldCount + 1 || state.Attempts[^1].Status is not null)
                throw new InvalidOperationException("Each new dispatch must have one reserved identity.");
            var totals = await AccountingAsync(campaign, requestId, record, ct);
            var sessionExhausted = sessionAttemptLimit is { } limit && totals["session_calls"]!.GetValue<long>() + reviewAttemptHeadroom > limit;
            var hardSession = _effectiveTransportPolicy?["enforce_session_limits"]?.GetValue<bool>() == true;
            var diagnostic = !hardSession && PlanningTokenThresholds.OptimizationAdvisory(_effectiveTransportPolicy);
            if (sessionExhausted && diagnostic)
            {
                OptimizationWarning(record, "physical_calls", totals["session_calls"]!.GetValue<long>() + reviewAttemptHeadroom,
                    sessionAttemptLimit!.Value, reviewAttemptHeadroom);
                sessionExhausted = false;
            }
            if (PlanningBenchmarkMeasurements.RetainsAllHttpFailures(record))
            {
                if (_effectiveTransportPolicy?["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() is not (>= 1 and <= 3) ||
                    state.Attempts.Count > _effectiveTransportPolicy["effective_retry_policy"]!["MaxAttempts"]!.GetValue<int>())
                    throw new InvalidOperationException("The captured request attempt allowance is exhausted.");
                if (IsRuntimePolicy(_effectiveTransportPolicy))
                {
                    if ((IsRuntimeSingleAttempt(_effectiveTransportPolicy) || _effectiveTransportPolicy!["disable_transport_retries"]?.GetValue<bool>() == true) &&
                        _effectiveTransportPolicy!["effective_retry_policy"]?["MaxAttempts"]?.GetValue<int>() != 1 ||
                        _effectiveTransportPolicy!["effective_retry_policy"]?["MaxUncertainRetries"]?.GetValue<int>() != 0)
                        throw new InvalidOperationException("The captured runtime policy forbids this retry.");
                }
                else
                {
                    if (!DateTimeOffset.TryParseExact(_effectiveTransportPolicy?["deadline_utc"]?.GetValue<string>(), "O",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var deadline))
                        throw new InvalidOperationException("The captured planning deadline is invalid.");
                    var elapsedOverrun = (time ?? TimeProvider.System).GetUtcNow() - deadline;
                    if (elapsedOverrun >= TimeSpan.Zero && !diagnostic)
                        throw new InvalidOperationException("The captured planning deadline cannot cover another HTTP attempt.");
                    if (diagnostic)
                    {
                        var elapsedTarget = _effectiveTransportPolicy?["max_elapsed_ms"]?.GetValue<long>();
                        if (elapsedTarget is not > 0) throw new InvalidOperationException("The captured planning elapsed target is invalid.");
                        if (elapsedOverrun >= TimeSpan.Zero)
                            OptimizationWarning(record, "elapsed_ms", checked(elapsedTarget.Value + (long)elapsedOverrun.TotalMilliseconds), elapsedTarget.Value);
                    }
                    var tokenLimit = _effectiveTransportPolicy?["max_total_tokens"]?.GetValue<long>()
                        ?? throw new InvalidOperationException("Captured cumulative token allowance is required.");
                    var reviewTokens = _effectiveTransportPolicy?["review_token_headroom"]?.GetValue<long>()
                        ?? throw new InvalidOperationException("Captured review token headroom is required.");
                    if (tokenLimit <= 0 || reviewTokens < 0) throw new InvalidOperationException("Captured token thresholds are invalid.");
                    if (tokenLimit > 15_000_000 || totals["session_token_upper_bound"]!.GetValue<long>() > tokenLimit - reviewTokens)
                    {
                        if (hardSession) throw PlanningLimit("planning_token_reservation_exhausted", tokenLimit,
                            totals["session_token_upper_bound"]!.GetValue<long>() + reviewTokens);
                        if (!PlanningTokenThresholds.Advisory(_effectiveTransportPolicy)) sessionExhausted = true;
                        else record["token_threshold_warning"] = new JsonObject { ["kind"] = "cumulative_tokens",
                            ["threshold"] = tokenLimit, ["current"] = totals["session_token_upper_bound"]!.DeepClone(),
                            ["review_headroom"] = reviewTokens };
                    }
                }
            }
            var retainedOtherReservations = _effectiveTransportPolicy?["retained_external_reservations_eur"]?.GetValue<decimal>() ?? 0;
            if (retainedOtherReservations < 0) throw new InvalidOperationException("Retained financial headroom cannot be negative.");
            await campaign.RequireRetainedApplicationHoldsAsync(retainedOtherReservations, ct);
            var ceiling = await campaign.CostCeilingAsync(ct);
            if (hardSession)
            {
                var reserve = _effectiveTransportPolicy!["execution_verification_reserve_eur"]!.GetValue<decimal>();
                var sessionCost = _effectiveTransportPolicy["planning_cost_limit_eur"]!.GetValue<decimal>();
                if (reserve < 0 || sessionCost <= 0) throw new InvalidOperationException("Invalid captured planning allowance.");
                if (totals["session_cost_upper_bound_eur"]!.GetValue<decimal>() > sessionCost)
                    throw PlanningLimit("planning_cost_reservation_exhausted", sessionCost, totals["session_cost_upper_bound_eur"]!.GetValue<decimal>());
                if (LLMUsageBudgetScope.CostSumExceeds(ceiling, totals["cost_upper_bound_eur"]!.GetValue<decimal>(), retainedOtherReservations, reserve))
                    throw PlanningLimit("execution_verification_reserve", ceiling,
                        LLMUsageBudgetScope.SumCostConservatively(totals["cost_upper_bound_eur"]!.GetValue<decimal>(), retainedOtherReservations, reserve));
            }
            var experimentalForecast = _effectiveTransportPolicy?["financial_forecast_mode"]?.ToString() == "experimental_warning";
            if (experimentalForecast && _effectiveTransportPolicy?["external_accounting_status"]?.ToString() == "unverified_external_holds")
                throw BenchmarkCampaign.AdmissionFailure("external_financial_history_incomplete");
            var currentExceeded = LLMUsageBudgetScope.CostSumExceeds(ceiling, totals["cost_upper_bound_eur"]!.GetValue<decimal>(), retainedOtherReservations);
            var moneyExceeded = LLMUsageBudgetScope.CostSumExceeds(ceiling, totals["cost_upper_bound_eur"]!.GetValue<decimal>(), reviewCostHeadroom, retainedOtherReservations);
            if (moneyExceeded && experimentalForecast && !currentExceeded)
            {
                record["financial_forecast_warning"] = new JsonObject { ["request_id"] = requestId, ["currency"] = "EUR", ["allowance"] = ceiling,
                    ["current_and_retained_exposure"] = LLMUsageBudgetScope.SumCostConservatively(totals["cost_upper_bound_eur"]!.GetValue<decimal>(), retainedOtherReservations),
                    ["future_review_forecast"] = reviewCostHeadroom, ["policy"] = "experimental_warning", ["current_reservation_enforced"] = true };
                moneyExceeded = false;
            }
            if (moneyExceeded || sessionExhausted)
            {
                campaign.BudgetExceeded(sessionExhausted);
                var error = new InvalidOperationException("The campaign or session cannot cover another HTTP attempt.");
                decimal? required = null;
                try { required = moneyExceeded ? LLMUsageBudgetScope.SumCostConservatively(totals["cost_upper_bound_eur"]!.GetValue<decimal>(), experimentalForecast ? 0m : reviewCostHeadroom, retainedOtherReservations)
                    : totals["session_calls"]!.GetValue<long>() + reviewAttemptHeadroom; }
                catch (OverflowException) { /* Exact admission already rejects unrepresentable exposure. */ }
                error.Data[BenchmarkCampaign.BudgetRefusalData] = new JsonObject {
                    ["request_id"] = requestId, ["scope"] = moneyExceeded ? "campaign_llm_budget" : "planning_session_calls",
                    ["origin"] = moneyExceeded ? "captured_campaign_ceiling" : "captured_session_attempt_limit",
                    ["limit_kind"] = moneyExceeded ? "estimated_cost" : "calls", ["allowance"] = moneyExceeded ? ceiling : sessionAttemptLimit,
                    ["required_reservation"] = required, ["estimated_cost_currency"] = moneyExceeded ? "EUR" : null,
                    ["dispatch_status"] = oldCount == 0 ? "not_started" : existing!["transport"]!["Attempts"]!.AsArray().Any(a => a?["Status"] is not null) ? "started" : "unknown",
                    ["attempt_dispatch_status"] = "not_started", ["denied_attempt_number"] = state.Attempts.Count,
                    ["mandatory_review_reserve_eur"] = reviewCostHeadroom, ["retained_external_reservations_eur"] = retainedOtherReservations };
                throw error;
            }
        }
        await campaign.SaveAsync(Collection, requestId, record, ct);
    }

    private static GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException PlanningLimit(string reason, decimal allowance, decimal required) =>
        new(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded, "The captured planning allowance cannot admit this HTTP attempt.",
            details: new JsonObject { ["reason"] = reason, ["dispatch_status"] = "not_started", ["allowance"] = allowance, ["required_reservation"] = required });

    private void OptimizationWarning(JsonObject record, string kind, long current, long threshold, int reviewHeadroom = 0)
    {
        PlanningTokenThresholds.Warn("benchmark_http", kind, current, threshold);
        var warning = new JsonObject { ["kind"] = kind, ["current"] = current, ["threshold"] = threshold,
            ["stage"] = "benchmark_http", ["basis"] = "retained_transport_accounting", ["request_id"] = requestId };
        if (reviewHeadroom > 0) warning["review_attempt_headroom"] = reviewHeadroom;
        var warnings = (record["optimization_threshold_warnings"] ??= new JsonArray()).AsArray();
        if (!warnings.Any(saved => JsonNode.DeepEquals(saved, warning))) warnings.Add(warning);
    }

    internal async Task<JsonObject> CompleteAsync(JsonObject usage, CancellationToken ct)
    {
        var record = await campaign.LoadAsync(Collection, requestId, ct) ?? throw new InvalidOperationException("HTTP reservation missing.");
        if (record["usage"] is JsonObject saved) return UsageWithReservations(record, saved);
        var attempts = record["transport"]!["Attempts"]!.AsArray();
        if (attempts.LastOrDefault()?["Status"]?.GetValue<int>() is not (>= 200 and < 300)) throw new InvalidOperationException("HTTP completion missing.");
        usage = UsageWithReservations(record, usage);
        record["usage"] = usage.DeepClone();
        await campaign.SaveAsync(Collection, requestId, record, ct);
        return usage;
    }

    internal static JsonObject UsageWithReservations(JsonObject record, JsonObject usage)
    {
        var attempts = record["transport"]!["Attempts"]!.AsArray();
        var unknown = attempts.Count(a => PlanningBenchmarkMeasurements.RequiresHttpUsageReservation(a?["Status"]?.GetValue<int>(), hasVerifiedUsage: true, PlanningBenchmarkMeasurements.RetainsAllHttpFailures(record)));
        usage = usage.DeepClone().AsObject();
        usage["transport_attempts"] = attempts.Count;
        usage["uncertain_attempts"] = unknown;
        usage["reserved_input_tokens"] = unknown * record["input_ceiling"]!.GetValue<long>();
        usage["reserved_output_tokens"] = unknown * record["output_ceiling"]!.GetValue<long>();
        usage["reserved_cost_eur"] = unknown * record["cost_ceiling_eur"]!.GetValue<decimal>();
        usage["benchmark_usage_bounded"] = true;
        return usage;
    }

    internal static async Task<JsonObject> AccountingAsync(BenchmarkCampaign campaign, string? requestId = null, JsonObject? replacement = null, CancellationToken ct = default, bool includeSessions = false)
    {
        var prefix = campaign.Id + ":";
        var rows = await campaign.HttpContributionsAsync(ct);
        if (requestId is not null && replacement is not null) rows[requestId] = BenchmarkCampaign.HttpContribution.From(replacement);
        var legacy = await campaign.Records.GetAsync("planning-evaluation-budgets", "benchmark", campaign.Id, BenchmarkCampaign.Author, ct);
        var snapshot = legacy is null ? null : JsonSerializer.Deserialize(legacy.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        if (snapshot is not null && snapshot.EstimatedCostCurrency != "EUR") throw new InvalidOperationException("The existing campaign ledger must be denominated in EUR.");
        long calls = snapshot?.Calls ?? 0, input = snapshot?.InputTokens ?? 0, output = snapshot?.OutputTokens ?? 0;
        long reservedInput = 0, reservedOutput = 0, uncertain = 0, sessionCalls = 0, sessionTokens = 0;
        decimal cost = snapshot?.EstimatedCost ?? 0, reservedCost = 0, sessionCost = 0;
        var session = requestId?.Split(':')[0] + ":";
        var perSession = includeSessions ? new Dictionary<string, JsonObject>(StringComparer.Ordinal) : null;
        foreach (var (key, row) in rows)
        {
            calls += row.Attempts;
            if (requestId is not null && key.StartsWith(session, StringComparison.Ordinal)) sessionCalls += row.Attempts;
            var pending = row.Pending;
            if (perSession is not null)
            {
                var owner = key.Split(':')[0];
                if (!perSession.TryGetValue(owner, out var item)) perSession[owner] = item = new() { ["sessionId"] = owner,
                    ["httpAttempts"] = 0L, ["unresolvedAttempts"] = 0L, ["measuredUsageTariffEstimateEur"] = 0m,
                    ["unresolvedReservationEur"] = 0m, ["zeroAttemptRequests"] = new JsonArray() };
                item["httpAttempts"] = item["httpAttempts"]!.GetValue<long>() + row.Attempts;
                item["unresolvedAttempts"] = item["unresolvedAttempts"]!.GetValue<long>() + pending;
                item["measuredUsageTariffEstimateEur"] = LLMUsageBudgetScope.SumCostConservatively(item["measuredUsageTariffEstimateEur"]!.GetValue<decimal>(), row.MeasuredCost ?? 0m);
                item["unresolvedReservationEur"] = LLMUsageBudgetScope.SumCostConservatively(new[] { item["unresolvedReservationEur"]!.GetValue<decimal>() }.Concat(Enumerable.Repeat(row.CostCeiling, pending)).ToArray());
                if (row.Attempts == 0) item["zeroAttemptRequests"]!.AsArray().Add(key);
            }
            if (requestId is not null && key.StartsWith(session, StringComparison.Ordinal))
            {
                sessionTokens = checked(sessionTokens + pending * checked(row.InputCeiling + row.OutputCeiling) +
                    row.InputTokens + row.OutputTokens);
                if (replacement?["effective_transport_policy"]?["enforce_session_limits"]?.GetValue<bool>() == true)
                    sessionCost = LLMUsageBudgetScope.SumCostConservatively(new[] { sessionCost, row.MeasuredCost ?? 0 }.Concat(Enumerable.Repeat(row.CostCeiling, pending)).ToArray());
            }
            uncertain += pending;
            reservedInput += pending * row.InputCeiling;
            reservedOutput += pending * row.OutputCeiling;
            reservedCost = LLMUsageBudgetScope.SumCostConservatively(new[] { reservedCost }.Concat(Enumerable.Repeat(row.CostCeiling, pending)).ToArray());
            if (row.MeasuredCost is { } measuredCost)
            {
                input += row.InputTokens; output += row.OutputTokens;
                cost = LLMUsageBudgetScope.SumCostConservatively(cost, measuredCost);
            }
        }
        if (requestId is not null)
        {
            var oldRequests = await campaign.OwnedKeysAsync("planning-evaluation-requests", ct, session);
            sessionCalls += oldRequests.Count(key => !rows.ContainsKey(key[prefix.Length..]));
        }
        var result = new JsonObject { ["calls"] = calls, ["session_calls"] = sessionCalls, ["known_input_tokens"] = input, ["known_output_tokens"] = output,
            ["known_cost_eur"] = cost, ["reserved_input_tokens"] = reservedInput, ["reserved_output_tokens"] = reservedOutput,
            ["reserved_cost_eur"] = reservedCost, ["cost_upper_bound_eur"] = LLMUsageBudgetScope.SumCostConservatively(cost, reservedCost), ["unknown_attempts"] = uncertain };
        if (perSession is not null)
        {
            result["sessions"] = new JsonArray(perSession.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)p.Value).ToArray());
            result["legacyAggregate"] = new JsonObject { ["present"] = snapshot is not null, ["estimatedCost"] = snapshot?.EstimatedCost,
                ["currency"] = snapshot?.EstimatedCostCurrency, ["indexedAttempts"] = snapshot?.JournaledAttempts?.Count,
                ["potentialOverlappingRequests"] = new JsonArray(rows.Keys.Where(key => snapshot?.JournaledAttempts?.Keys.Any(id => id == key || id.StartsWith(key + ":", StringComparison.Ordinal)) == true)
                    .Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) };
            result["providerInvoicedCost"] = null;
            result["basis"] = "Measured usage × captured tariffs and unresolved reservations; not provider invoices. Group sums may round upward differently. Zero-attempt campaign records reserve no money. No reservation is released by inspection.";
        }
        // Preserve historical accounting hashes when no new captured policy is being admitted.
        if (replacement is not null && PlanningBenchmarkMeasurements.RetainsAllHttpFailures(replacement))
            result["session_token_upper_bound"] = sessionTokens;
        if (replacement?["effective_transport_policy"]?["enforce_session_limits"]?.GetValue<bool>() == true)
            result["session_cost_upper_bound_eur"] = sessionCost;
        return result;
    }
}
