using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Session = "uncertain-session", RequestId = Session + ":1:original";
    private const string Operator = "authorized-benchmark-operator", Authorization = "Permanently retain this exact inconclusive run and its full reservation.";
    private const string Closures = "planning-evaluation-closures";
    private const string Http500 = BenchmarkCampaign.SingleHttp500Retention;
    private const string Http502 = BenchmarkCampaign.SingleHttp502Retention;
    private const string Http504 = BenchmarkCampaign.SingleHttp504Retention;
    private const string Interrupted = BenchmarkCampaign.InterruptedProcessRetention;
    private static string InterruptedKey => new string('a', 40) + ":fixture:cohort:diagnostic:fixture:1";
    private static string InterruptedSession => PlanningGraphCompiler.Fingerprint("retention:" + InterruptedKey);
    private static string InterruptedRequest => InterruptedSession + ":1:original";
    private static string InterruptedConfigurationKey => "corpus-settings:" + new string('a', 40) + ":fixture:control:cohort:diagnostic";
    private const string ErrorBody = """{"errorCode":"upstream_failure","errorMessage":"Provider unavailable.","errorDetail":"No completion receipt."}""";

    [Theory]
    [InlineData(false, null, "CallHeadroom")]
    [InlineData(true, null, "CallHeadroom")]
    [InlineData(false, null, "Unknown")]
    [InlineData(true, null, "Unknown")]
    [InlineData(false, null, "Transport")]
    [InlineData(true, null, "Transport")]
    [InlineData(false, null, "Timeout")]
    [InlineData(false, 500, "ServiceUnavailable")]
    [InlineData(false, 502, "ServiceUnavailable")]
    [InlineData(true, 502, "ServiceUnavailable")]
    [InlineData(false, 503, "ServiceUnavailable")]
    [InlineData(false, 504, "Timeout")]
    public async Task UnstartedPlanningRetentionPreservesZeroAttemptEvidenceAndSurvivesInterruptedPublication(bool afterCommit, int? status, string failureKind)
    {
        var records = new Records(); var campaign = failureKind == "CallHeadroom"
            ? await SeedCallHeadroomUnstartedAsync(records) : await SeedUnstartedPlanningAsync(records);
        if (failureKind is not ("Unknown" or "CallHeadroom"))
        {
            var failure = (await campaign.LoadAsync("planning-evaluation-failures", RequestId, Ct))!;
            failure["status_code"] = status;
            failure["kind"] = failureKind;
            failure["retryable"] = true;
            await campaign.SaveAsync("planning-evaluation-failures", RequestId, failure, Ct);
        }
        var original = records.Snapshot();
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var kind = BenchmarkCampaign.UnstartedPlanningRetention;
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        Assert.Equal(original, records.Snapshot());
        Assert.Equal("no_reserved_physical_attempt", proposal["evidence"]!["outcome_evidence"]!.ToString());
        Assert.Equal(failureKind == "CallHeadroom" ? 63 : 0, accounting["calls"]!.GetValue<long>());
        Assert.Equal(failureKind == "CallHeadroom" ? 42m : 0m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        var hash = proposal["evidence_hash"]!.ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ApplyPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, new string('0', 64), Operator, Authorization, Ct, kind));
        records.FailClosure = true; records.FailAfterCommit = afterCommit;
        await Assert.ThrowsAsync<IOException>(() => campaign.ApplyPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind));
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        records.FailClosure = false;
        var restarted = new BenchmarkCampaign(records, "retention");
        await restarted.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind);
        await restarted.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind);
        Assert.Single(await records.ListAsync(Closures, "benchmark", BenchmarkCampaign.Author, Ct));
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        Assert.Null(await restarted.LoadAsync("planning-evaluation-receipts", RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallAsync(new() { ClientRequestId = RequestId },
            _ => throw new Exception("Must not admit"), _ => throw new Exception("Must not dispatch"), Ct));
    }

    [Theory]
    [InlineData("budget_available")]
    [InlineData("budget_headroom")]
    [InlineData("budget_limit")]
    [InlineData("budget_mode")]
    [InlineData("budget_exception")]
    [InlineData("attempt")]
    [InlineData("fingerprint")]
    [InlineData("delay")]
    [InlineData("status")]
    [InlineData("kind")]
    [InlineData("count")]
    [InlineData("retryable")]
    [InlineData("policy")]
    [InlineData("usage")]
    [InlineData("receipt")]
    [InlineData("http_success")]
    [InlineData("http_client_error")]
    [InlineData("http_kind")]
    [InlineData("http_retryable")]
    [InlineData("http_exhausted")]
    [InlineData("http_retry_after")]
    [InlineData("http_provider_code")]
    [InlineData("http_attempt")]
    [InlineData("http_fingerprint")]
    [InlineData("http_usage")]
    [InlineData("http_receipt")]
    public async Task UnstartedPlanningRetentionRejectsAnyConflictingDispatchEvidence(string defect)
    {
        var records = new Records(); var campaign = defect.StartsWith("budget_", StringComparison.Ordinal)
            ? await SeedCallHeadroomUnstartedAsync(records, defect == "budget_available" ? 62 : 63) : await SeedUnstartedPlanningAsync(records);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", RequestId, Ct))!;
        if (defect.StartsWith("http_", StringComparison.Ordinal))
        {
            failure["kind"] = "ServiceUnavailable"; failure["status_code"] = 502; failure["retryable"] = true;
        }
        switch (defect)
        {
            case "budget_available": break;
            case "budget_headroom": journal["construction_review_attempt_headroom"] = 0; break;
            case "budget_limit": journal["session_attempt_limit"] = 65; break;
            case "budget_mode": journal["effective_transport_policy"]!.AsObject().Remove(PlanningTokenThresholds.OptionName); break;
            case "budget_exception": failure["exception_type"] = "LLMClientException"; break;
            case "attempt": journal["transport"]!["Attempts"]!.AsArray().Add(new JsonObject { ["Id"] = RequestId + ":reserved" }); break;
            case "fingerprint": journal["transport"]!["Fingerprint"] = new string('a', 64); break;
            case "delay": journal["transport"]!["DelayMilliseconds"] = 1d; break;
            case "status": failure["status_code"] = 500; break;
            case "kind": failure["kind"] = "Timeout"; break;
            case "count": failure["attempt_count"] = 0; break;
            case "retryable": failure["retryable"] = true; break;
            case "policy": journal["effective_transport_policy"]!["effective_retry_policy"]!["MaxUncertainRetries"] = 1; break;
            case "usage": journal["usage"] = new JsonObject(); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new(), Ct); break;
            case "http_success": failure["status_code"] = 200; break;
            case "http_client_error": failure["status_code"] = 401; break;
            case "http_kind": failure["kind"] = "Timeout"; break;
            case "http_retryable": failure["retryable"] = false; break;
            case "http_exhausted": failure["retry_exhausted"] = true; break;
            case "http_retry_after": failure["retry_after_ms"] = 1000; break;
            case "http_provider_code": failure["safe_provider_code"] = "unverified"; break;
            case "http_attempt": journal["transport"]!["Attempts"]!.AsArray().Add(new JsonObject { ["Id"] = RequestId + ":reserved" }); break;
            case "http_fingerprint": journal["transport"]!["Fingerprint"] = new string('a', 64); break;
            case "http_usage": journal["usage"] = new JsonObject(); break;
            case "http_receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new(), Ct); break;
        }
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(journal["effective_transport_policy"]!.ToJsonString());
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, failure, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, BenchmarkCampaign.UnstartedPlanningRetention));
        Assert.Equal(before, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedCallHeadroomUnstartedAsync(Records records, int calls = 63)
    {
        var campaign = await SeedUnstartedPlanningAsync(records);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        run["session"]!["request"]!["options"]![PlanningTokenThresholds.OptionName] = "advisory";
        run["session"]!["pendingCall"]!["request"]!["requiredReviewCalls"] = 1;
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, run, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", RequestId, run["session"]!["pendingCall"]!["request"]!.DeepClone().AsObject(), Ct);
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy[PlanningTokenThresholds.OptionName] = "advisory";
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["effective_transport_policy"]![PlanningTokenThresholds.OptionName] = "advisory";
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(journal["effective_transport_policy"]!.ToJsonString());
        journal["construction_review_attempt_headroom"] = 1;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        for (var offset = 0; offset < calls; offset += 3)
        {
            var id = Session + ":prior-" + offset;
            var prior = journal.DeepClone().AsObject();
            var count = Math.Min(3, calls - offset);
            prior["transport"]!["Fingerprint"] = new string('a', 64);
            prior["transport"]!["Attempts"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
            {
                ["Id"] = id + ":attempt-" + i, ["Status"] = i == count - 1 ? 200 : 500,
                ["Body"] = i == count - 1 ? "{}" : ErrorBody
            }).ToArray());
            prior["usage"] = new JsonObject { ["input_tokens"] = 1L, ["output_tokens"] = 1L, ["benchmark_cost_eur"] = .01m };
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, prior, Ct);
        }
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, new()
        {
            ["stage"] = "dispatch", ["exception_type"] = "InvalidOperationException", ["kind"] = null,
            ["status_code"] = null, ["safe_provider_code"] = null, ["retryable"] = null,
            ["attempt_count"] = null, ["retry_exhausted"] = null, ["retry_after_ms"] = null
        }, Ct);
        return campaign;
    }

    private static async Task<BenchmarkCampaign> SeedUnstartedPlanningAsync(Records records)
    {
        var campaign = await SeedCapturedRetriesAsync(records, 0, false, reviewHeadroom: 2);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"] = JsonSerializer.SerializeToNode(new LLMHttpRetryState(), LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, new()
        {
            ["stage"] = "dispatch", ["exception_type"] = "LLMClientException", ["kind"] = "Unknown",
            ["status_code"] = null, ["safe_provider_code"] = null, ["retryable"] = false,
            ["attempt_count"] = 1, ["retry_exhausted"] = false, ["retry_after_ms"] = null
        }, Ct);
        return campaign;
    }

    [Fact]
    public async Task RetentionRequiresTheCapturedActiveFormatWithoutChangingReservations()
    {
        var records = new Records(); var campaign = await SeedCapturedRetriesAsync(records, 1, true, reviewHeadroom: 4);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.NotNull(await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct,
            BenchmarkCampaign.ExhaustedPlanningRetriesRetention));
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy["planning_format"] = 10;
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task CapturedRetryTerminalRetentionBindsEveryAttemptAndNeverReleasesUnknownUsage(int count, bool unknownLast)
    {
        var records = new Records(); var campaign = await SeedCapturedRetriesAsync(records, count, unknownLast);
        var before = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(count, accounting["unknown_attempts"]!.GetValue<long>());
        Assert.Equal((decimal)count, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502));
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct,
            BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
        await campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
        var restarted = new BenchmarkCampaign(records, "retention");
        await restarted.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
        Assert.Single(await records.ListAsync(Closures, "benchmark", BenchmarkCampaign.Author, Ct));
        Assert.Equal(before, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
    }

    [Theory]
    [InlineData("prior_success")]
    [InlineData("prior_unknown")]
    [InlineData("duplicate_identity")]
    [InlineData("policy")]
    [InlineData("usage")]
    [InlineData("receipt")]
    [InlineData("cancelled")]
    public async Task CapturedRetryRetentionRejectsConflictingOrIncompleteEvidence(string defect)
    {
        var records = new Records(); var campaign = await SeedCapturedRetriesAsync(records, 3, true);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        switch (defect)
        {
            case "prior_success": journal["transport"]!["Attempts"]![0]!["Status"] = 200; break;
            case "prior_unknown": journal["transport"]!["Attempts"]![0]!["Status"] = null; break;
            case "duplicate_identity": journal["transport"]!["Attempts"]![1]!["Id"] = journal["transport"]!["Attempts"]![0]!["Id"]!.DeepClone(); break;
            case "policy": journal["effective_transport_policy"]!["effective_retry_policy"]!["MaxUncertainRetries"] = 1; break;
            case "usage": journal["usage"] = new JsonObject(); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new(), Ct); break;
            case "cancelled": journal["transport"]!["Attempts"]![2]!["Failure"] = "cancelled"; break;
        }
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(journal["effective_transport_policy"]!.ToJsonString());
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention));
        Assert.Equal(before, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedCapturedRetriesAsync(Records records, int count, bool unknownLast,
        int reviewHeadroom = 2)
    {
        var campaign = await SeedAsync(records);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        var state = run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        var captured = PlanningTransportPolicies.Capture(state.Request);
        state.Request.Options["benchmark_planning_elapsed_ms"] = 18_000_000L;
        state.Request.Options["llm_budget"] = new JsonObject { ["max_total_tokens"] = 15_000_000L, ["max_elapsed_ms"] = 18_000_000L };
        state.PendingCall!.Request.RequiredReviewCalls = reviewHeadroom;
        run["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, run, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", RequestId,
            JsonSerializer.SerializeToNode(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        foreach (var name in new[] { PlanningTransportPolicies.OptionName, "benchmark_planning_elapsed_ms", "llm_budget" })
            policy[name] = state.Request.Options[name]!.DeepClone();
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        var retry = new LLMProviderRetryPolicyOptions { MaxAttempts = captured.MaxAttempts, MaxUncertainRetries = 0,
            AttemptTimeoutMilliseconds = captured.AttemptTimeoutMilliseconds, BaseDelayMilliseconds = captured.BaseDelayMilliseconds,
            MaxDelayMilliseconds = captured.MaxDelayMilliseconds, MaxTotalDelayMilliseconds = captured.MaxTotalDelayMilliseconds };
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var effective = new JsonObject { ["version"] = 2, ["disable_transport_retries"] = false, ["allow_http_recovery"] = true,
            ["planning_transport_policy"] = policy[PlanningTransportPolicies.OptionName]!.DeepClone(),
            ["effective_retry_policy"] = JsonSerializer.SerializeToNode(retry, LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions),
            ["max_total_tokens"] = 15_000_000L, ["review_token_headroom"] = 100_000L,
            ["max_elapsed_ms"] = 18_000_000L, ["deadline_utc"] = DateTimeOffset.UtcNow.AddHours(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        journal["effective_transport_policy"] = effective;
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(effective.ToJsonString());
        var attempts = new JsonArray();
        for (var index = 0; index < count; index++)
            attempts.Add((JsonNode)new JsonObject { ["Id"] = RequestId + ":attempt-" + index,
                ["Status"] = unknownLast && index == count - 1 ? null : index == 0 ? 429 : 502,
                ["Body"] = unknownLast && index == count - 1 ? null : ErrorBody,
                ["Failure"] = unknownLast && index == count - 1 ? "transport" : null });
        journal["transport"]!["Attempts"] = attempts;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, new()
        {
            ["stage"] = "dispatch", ["kind"] = unknownLast ? "Transport" : "ServiceUnavailable",
            ["retryable"] = true, ["retry_exhausted"] = !unknownLast, ["attempt_count"] = count,
            ["status_code"] = unknownLast ? null : 502
        }, Ct);
        return campaign;
    }

    [Fact]
    public async Task InterruptedRetentionBindsMissingOutcomeWithoutFabricatingFailureOrResult()
    {
        var records = new Records(); var campaign = await SeedAsync(records, corpus: true, retentionKind: Interrupted);
        var before = records.Snapshot(); var writes = records.Writes;
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, Interrupted);
        var evidence = proposal["evidence"]!.AsObject();
        Assert.Equal("reserved_attempt_without_persisted_outcome", evidence["outcome_evidence"]!.ToString());
        Assert.Equal("corpus_key_and_frozen_configuration", evidence["source_evidence"]!.ToString());
        Assert.Equal(new string('a', 40), evidence["source"]!.ToString());
        Assert.False(evidence.ContainsKey("failure_hash"));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
        foreach (var kind in new string?[] { null, Http500, Http502, Http504 })
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
                "planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, kind));
        await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, Interrupted);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedKey, Ct))!;
        Assert.Equal(PlanningStatus.Generating, run["session"]!["status"]!.ToString());
        Assert.Null(run["result"]); Assert.Null(run["source"]);
        Assert.Null(await campaign.LoadAsync("planning-evaluation-failures", InterruptedRequest, Ct));
    }

    [Theory]
    [InlineData("construction_intent")]
    [InlineData("construction_select")]
    [InlineData("construction_discovery")]
    [InlineData("construction_draft")]
    [InlineData("construction_local_judge")]
    [InlineData("construction_global_judge")]
    [InlineData("replan")]
    public async Task InterruptedRetentionAcceptsMatchingCurrentPlanningPhase(string purpose)
    {
        var records = new Records(); var campaign = await SeedAsync(records, corpus: true, retentionKind: Interrupted);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedKey, Ct))!;
        run["session"]!["phase"] = purpose == "replan" ? PlanningPhase.Replanning : purpose;
        run["session"]!["pendingCall"]!["purpose"] = purpose;
        run["response_measurements"] = new JsonObject { [InterruptedSession + ":0:prior"] = new JsonObject() };
        run["usage_receipts"] = new JsonObject { [InterruptedSession + ":0:prior"] = new JsonObject() };
        await campaign.SaveAsync("planning-evaluation-runs", InterruptedKey, run, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", InterruptedRequest, run["session"]!["pendingCall"]!["request"]!.DeepClone().AsObject(), Ct);
        var before = records.Snapshot();
        Assert.NotNull(await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, Interrupted));
        Assert.Equal(before, records.Snapshot());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("phase")]
    [InlineData("result")]
    [InlineData("response_measurement")]
    [InlineData("run_usage")]
    [InlineData("first_proposal")]
    [InlineData("final_review")]
    [InlineData("approved")]
    [InlineData("execution")]
    [InlineData("failure")]
    [InlineData("receipt")]
    [InlineData("usage")]
    [InlineData("http_status")]
    [InlineData("http_body")]
    [InlineData("http_failure")]
    [InlineData("http_headers")]
    [InlineData("attempt_identity")]
    [InlineData("http_fingerprint")]
    [InlineData("two_attempts")]
    [InlineData("request")]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("case")]
    [InlineData("policy")]
    [InlineData("configuration")]
    [InlineData("policy_hash")]
    [InlineData("recovery")]
    [InlineData("retry")]
    [InlineData("missing_policy")]
    [InlineData("input_reservation")]
    [InlineData("output_reservation")]
    public async Task InterruptedRetentionRejectsChangedEvidenceOrAnyPersistedOutcome(string defect)
    {
        var records = new Records(); var campaign = await SeedAsync(records, corpus: true, retentionKind: Interrupted);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedKey, Ct))!;
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, InterruptedRequest, Ct))!;
        var attempt = journal["transport"]!["Attempts"]![0]!;
        var policy = journal["effective_transport_policy"]!;
        switch (defect)
        {
            case "status": run["session"]!["status"] = PlanningStatus.Stopped; break;
            case "phase": run["session"]!["phase"] = PlanningPhase.Replanning; break;
            case "response_measurement": run["response_measurements"] = new JsonObject { [InterruptedRequest] = new JsonObject() }; break;
            case "run_usage": run["usage_receipts"] = new JsonObject { [InterruptedRequest] = new JsonObject() }; break;
            case "first_proposal": run["first_proposal"] = new JsonObject { ["request_id"] = InterruptedRequest }; break;
            case "result": run["result"] = new JsonObject(); break;
            case "final_review": run["final_review"] = true; break;
            case "approved": run["session"]!["approvedHash"] = "approved"; break;
            case "execution": run["execution_started"] = true; break;
            case "failure": await campaign.SaveAsync("planning-evaluation-failures", InterruptedRequest, new(), Ct); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", InterruptedRequest, new(), Ct); break;
            case "usage": journal["usage"] = new JsonObject(); break;
            case "http_status": attempt["Status"] = 200; break;
            case "http_body": attempt["Body"] = "{}"; break;
            case "http_failure": attempt["Failure"] = "timeout"; break;
            case "http_headers": attempt["ContentType"] = "application/json"; break;
            case "attempt_identity": attempt["Id"] = "other:attempt"; break;
            case "http_fingerprint": journal["transport"]!["Fingerprint"] = "missing"; break;
            case "two_attempts": journal["transport"]!["Attempts"]!.AsArray().Add(attempt.DeepClone()); break;
            case "request": run["session"]!["pendingCall"]!["request"]!["prompt"] = "changed"; break;
            case "tenant": run["session"]!["request"]!["tenantId"] = "other"; break;
            case "source": run["source"] = new string('b', 40); break;
            case "case": run["session"]!["request"]!["name"] = "changed"; break;
            case "policy": await campaign.SaveAsync("planning-evaluation-session-policy", InterruptedSession, new(), Ct); break;
            case "configuration": await campaign.SaveAsync("planning-evaluation-configuration", InterruptedConfigurationKey, new(), Ct); break;
            case "policy_hash": journal["effective_transport_policy_hash"] = new string('0', 64); break;
            case "recovery": policy["allow_http_recovery"] = false; break;
            case "retry": policy["effective_retry_policy"]!["MaxAttempts"] = 2; break;
            case "missing_policy": journal.Remove("effective_transport_policy"); journal.Remove("effective_transport_policy_hash"); break;
            case "input_reservation": journal["input_ceiling"] = 1; break;
            case "output_reservation": journal["output_ceiling"] = 1; break;
        }
        if (defect is "recovery" or "retry") journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(policy.ToJsonString());
        await campaign.SaveAsync("planning-evaluation-runs", InterruptedKey, run, Ct);
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, InterruptedRequest, journal, Ct);
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAnyAsync<Exception>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, Interrupted));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("configuration")]
    [InlineData("accounting")]
    [InlineData("hash")]
    public async Task InterruptedApplyRejectsStaleEvidence(string changed)
    {
        var records = new Records(); var campaign = await SeedAsync(records, corpus: true, retentionKind: Interrupted);
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, Interrupted);
        if (changed == "run")
        {
            var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedKey, Ct))!; run["additional_evidence"] = true;
            await campaign.SaveAsync("planning-evaluation-runs", InterruptedKey, run, Ct);
        }
        if (changed == "configuration") await campaign.SaveAsync("planning-evaluation-configuration", InterruptedConfigurationKey, new(), Ct);
        if (changed == "accounting") await new BenchmarkHttpJournal(campaign, "other:1", 1, 1, .1m).SaveAsync(new() { Attempts = [new() { Id = "other" }] }, Ct);
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAnyAsync<Exception>(() => campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest,
            changed == "hash" ? new string('0', 64) : proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, Interrupted));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedProcessClosurePublicationIsAtomicAcrossRestart(bool afterCommit)
    {
        var records = new Records(); var campaign = await SeedAsync(records, corpus: true, retentionKind: Interrupted);
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, Interrupted);
        var before = records.Snapshot(); var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        records.FailClosure = true; records.FailAfterCommit = afterCommit;
        await Assert.ThrowsAsync<IOException>(() => campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, Interrupted));
        Assert.Equal(before, records.Snapshot(excludeClosures: true));
        records.FailClosure = false;
        var restarted = new BenchmarkCampaign(records, "retention");
        await restarted.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, Interrupted);
        Assert.Single(await records.ListAsync(Closures, "benchmark", BenchmarkCampaign.Author, Ct));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(InterruptedSession, Ct));
    }

    [Fact]
    public async Task InterruptedProcessClosureIsEncryptedAndTenantIsolated()
    {
        var root = Directory.CreateTempSubdirectory("interrupted-retention-encrypted-").FullName;
        try
        {
            var path = Path.Combine(root, "vault.db"); var store = new KeyVaultRecordStore(path);
            var campaign = await SeedAsync(store, corpus: true, retentionKind: Interrupted);
            var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest, Ct, Interrupted);
            const string secret = "interrupted-private-authorization-8af37d";
            await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedKey, InterruptedRequest,
                proposal["evidence_hash"]!.ToString(), Operator, secret, Ct, Interrupted);
            var restored = new KeyVaultRecordStore(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkCampaign(restored, "retention").RequireRunNotRetainedAsync(InterruptedSession, Ct));
            Assert.Null(await restored.GetAsync(Closures, "other-tenant", "retention:" + InterruptedRequest, BenchmarkCampaign.Author, Ct));
            foreach (var file in Directory.EnumerateFiles(root))
                Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<BenchmarkCampaign> SeedAsync(IKeyVaultRecordStore records, bool corpus = false,
        string? retentionKind = null)
    {
        var campaign = new BenchmarkCampaign(records, "retention");
        var interrupted = retentionKind == Interrupted;
        var session = interrupted ? InterruptedSession : Session;
        var requestId = interrupted ? InterruptedRequest : RequestId;
        var state = new PlanningSession
        {
            Request = new() { TenantId = "benchmark", SessionId = session, Name = interrupted ? "fixture" : "retention", Prompt = "Original exact intent.",
                Options = new() { ["benchmark_planning_elapsed_ms"] = 18_000_000L,
                    ["llm_budget"] = new JsonObject { ["max_total_tokens"] = 15_000_000L, ["max_elapsed_ms"] = 18_000_000L } } },
            Construction = new()
        };
        GnOuGo.Flow.Planning.PlanningSessionCreation.Capture(state);
        state.Revision = 1; state.ModelCalls = 1;
        state.Status = interrupted ? PlanningStatus.Generating : PlanningStatus.Stopped;
        var request = new LLMRequest { ClientRequestId = requestId, Prompt = "Original exact intent.", MaxTokens = 8192,
            UseBackgroundMode = false, RequiredReviewCalls = 2,
            StructuredOutputSchema = new JsonObject { ["title"] = "construction_intent", ["type"] = "object" } };
        state.PendingCall = new() { Id = requestId, Purpose = "construction_intent", Request = request };
        if (interrupted) state.Phase = "construction_intent";
        else state.Diagnostics.Add(new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Completion remains unknown."));
        var captured = PlanningTransportPolicies.Read(state.Request)!;
        var retry = new LLMProviderRetryPolicyOptions
        {
            MaxAttempts = captured.MaxAttempts, MaxUncertainRetries = 0, AttemptTimeoutMilliseconds = captured.AttemptTimeoutMilliseconds,
            BaseDelayMilliseconds = captured.BaseDelayMilliseconds, MaxDelayMilliseconds = captured.MaxDelayMilliseconds,
            MaxTotalDelayMilliseconds = captured.MaxTotalDelayMilliseconds
        };
        var effective = new JsonObject { ["version"] = 2, ["disable_transport_retries"] = false, ["allow_http_recovery"] = true,
            ["planning_transport_policy"] = state.Request.Options[PlanningTransportPolicies.OptionName]!.DeepClone(),
            ["effective_retry_policy"] = JsonSerializer.SerializeToNode(retry, LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions),
            ["max_total_tokens"] = 15_000_000L, ["review_token_headroom"] = 100_000L,
            ["max_elapsed_ms"] = 18_000_000L, ["deadline_utc"] = DateTimeOffset.UtcNow.AddHours(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        await campaign.SaveAsync("planning-evaluation-requests", requestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var journal = new BenchmarkHttpJournal(campaign, requestId, 6651, 8192, 1m, 64, effectiveTransportPolicy: effective);
        await journal.PrepareAsync(Ct, new JsonObject { ["source_currency"] = "USD", ["target_currency"] = "EUR", ["rate"] = .9m,
            ["source"] = "retained-test-authority", ["as_of_utc"] = DateTimeOffset.UtcNow.AddDays(-1) });
        await journal.SaveAsync(new() { Fingerprint = new string('a', 64), Attempts = [new() { Id = requestId + ":attempt" }] }, Ct);
        if (!interrupted) await campaign.SaveAsync("planning-evaluation-failures", requestId, new()
        { ["stage"] = "dispatch", ["kind"] = "Transport", ["retryable"] = true, ["attempt_count"] = 1, ["retry_exhausted"] = false }, Ct);
        await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy",
            JsonSerializer.SerializeToNode(retry, LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions)!.AsObject(), Ct);
        var policy = new JsonObject { ["planning_format"] = 11, ["construction_review_headroom"] = 2,
            ["max_physical_attempts"] = state.Request.MaxModelCalls, ["max_input_tokens"] = state.Request.Generation.MaxInputTokensPerRequest,
            ["max_output_tokens"] = state.Request.Generation.MaxOutputTokens, ["max_corrections"] = state.Request.MaxReplanAttempts };
        foreach (var key in new[] { "construction_limits", PlanningTransportPolicies.OptionName,
            "benchmark_planning_elapsed_ms", "llm_budget" }) policy[key] = state.Request.Options[key]?.DeepClone();
        await campaign.SaveAsync("planning-evaluation-session-policy", session, policy, Ct);
        var run = new JsonObject { ["source"] = new string('a', 40), ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession),
            ["result"] = new JsonObject { ["status"] = "stopped", ["execution_status"] = "not_started", ["execution_correct"] = false } };
        if (interrupted)
        {
            run.Remove("source"); run.Remove("result"); run["final_review"] = false;
            await campaign.SaveAsync("planning-evaluation-configuration", InterruptedConfigurationKey, PlanningBenchmarkMeasurements.Configuration(state.Request), Ct);
        }
        await campaign.SaveAsync(corpus ? "planning-evaluation-runs" : SchemaPortabilityCampaign.Collection,
            interrupted ? InterruptedKey : corpus ? "source:pilot:case:1" : "run:" + Session, run, Ct);
        return campaign;
    }

    private sealed class Records : IKeyVaultRecordStore
    {
        private readonly Dictionary<(string Collection, string Tenant, string Key, string Author), KeyVaultRecordValue> _rows = [];
        internal int Writes; internal bool FailClosure, FailAfterCommit;
        internal string Snapshot(bool excludeClosures = false) => string.Join('\n', _rows.Values
            .Where(r => !excludeClosures || r.Collection != Closures).OrderBy(r => r.Collection).ThenBy(r => r.Key).Select(r => r.Value));
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default)
            => Task.FromResult(_rows.GetValueOrDefault((c, t, k, a)));
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (c == Closures && FailClosure && !FailAfterCommit) throw new IOException("Injected pre-publication failure.");
            var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _rows[(c, t, k, a)] = row; Writes++;
            if (c == Closures && FailClosure) throw new IOException("Injected lost publication acknowledgement.");
            return Task.FromResult(row);
        }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Where(r => r.Key.Collection == c && r.Key.Tenant == t && r.Key.Author == a).Select(r => r.Value).ToArray());
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
