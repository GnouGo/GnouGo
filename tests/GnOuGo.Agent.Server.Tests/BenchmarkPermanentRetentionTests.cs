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
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnstartedPlanningRetentionPreservesZeroAttemptEvidenceAndSurvivesInterruptedPublication(bool afterCommit)
    {
        var records = new Records(); var campaign = await SeedUnstartedPlanningAsync(records);
        var original = records.Snapshot();
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var kind = BenchmarkCampaign.UnstartedPlanningRetention;
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        Assert.Equal(original, records.Snapshot());
        Assert.Equal("no_reserved_physical_attempt", proposal["evidence"]!["outcome_evidence"]!.ToString());
        Assert.Equal(0, accounting["calls"]!.GetValue<long>());
        Assert.Equal(0m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
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
    public async Task UnstartedPlanningRetentionRejectsAnyConflictingDispatchEvidence(string defect)
    {
        var records = new Records(); var campaign = await SeedUnstartedPlanningAsync(records);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", RequestId, Ct))!;
        switch (defect)
        {
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
        }
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(journal["effective_transport_policy"]!.ToJsonString());
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, failure, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, BenchmarkCampaign.UnstartedPlanningRetention));
        Assert.Equal(before, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedUnstartedPlanningAsync(Records records)
    {
        var campaign = await SeedCapturedRetriesAsync(records, 0, false, presentation: 11);
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

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task SourceWindowHeadroomRetentionRequiresItsCapturedPresentationWithoutChangingReservations(int presentation)
    {
        var records = new Records();
        var campaign = await SeedCapturedRetriesAsync(records, 1, true, presentation: presentation, reviewHeadroom: 4);
        var before = records.Snapshot();
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var inspection = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session,
            RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention);
        Assert.NotNull(inspection["evidence_hash"]);
        Assert.Equal(before, records.Snapshot());
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy["construction_presentation_version"] = presentation + 1;
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, BenchmarkCampaign.ExhaustedPlanningRetriesRetention));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        policy.Remove("construction_presentation_version");
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(RequestId, Ct));
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
        int? presentation = null, int reviewHeadroom = 2)
    {
        var campaign = await SeedAsync(records, true, retentionKind: Http502);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        var state = run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        var captured = PlanningTransportPolicies.Capture(state.Request);
        state.Request.Options["benchmark_planning_elapsed_ms"] = 18_000_000L;
        state.Request.Options["llm_budget"] = new JsonObject { ["max_total_tokens"] = 15_000_000L, ["max_elapsed_ms"] = 18_000_000L };
        state.PendingCall!.Request.RequiredReviewCalls = reviewHeadroom;
        if (presentation is { } version) state.Request.Options["construction_presentation_version"] = version;
        run["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, run, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", RequestId,
            JsonSerializer.SerializeToNode(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        foreach (var name in new[] { PlanningTransportPolicies.OptionName, "benchmark_planning_elapsed_ms", "llm_budget" })
            policy[name] = state.Request.Options[name]!.DeepClone();
        if (presentation is { } capturedPresentation) policy["construction_presentation_version"] = capturedPresentation;
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        var retry = new LLMProviderRetryPolicyOptions { MaxAttempts = captured.MaxAttempts, MaxUncertainRetries = 0 };
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

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, Http500)]
    [InlineData(true, false, Http502)]
    [InlineData(true, false, Http504)]
    [InlineData(true, true, Http500)]
    [InlineData(true, true, Http502)]
    [InlineData(true, true, Http504)]
    [InlineData(true, true, Interrupted)]
    public async Task ExplicitRetentionPreservesEvidenceAndAccountingWhilePermanentlyBlockingTheRun(bool capturedTransport, bool corpus, string? kind)
    {
        var requestId = kind == Interrupted ? InterruptedRequest : RequestId;
        var session = kind == Interrupted ? InterruptedSession : Session;
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport, corpus, kind);
        var collection = corpus ? "planning-evaluation-runs" : SchemaPortabilityCampaign.Collection;
        var key = kind == Interrupted ? InterruptedKey : corpus ? "source:pilot:case:1" : "run:" + session;
        var original = records.Snapshot(); var writes = records.Writes;
        var proposal = await campaign.InspectPermanentRetentionAsync(collection, key, requestId, Ct, kind);
        Assert.Equal(writes, records.Writes);
        Assert.Equal(kind is not null, proposal["evidence"]!.AsObject().ContainsKey("retention_kind"));
        Assert.Equal(kind, proposal["evidence"]!["retention_kind"]?.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(requestId, Ct));
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(1m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        var hash = proposal["evidence_hash"]!.ToString();
        var closure = await campaign.ApplyPermanentRetentionAsync(collection, key, requestId, hash, Operator, Authorization, Ct, kind);
        Assert.Equal("inconclusive", closure["outcome"]!.ToString());
        Assert.Equal(writes + 1, records.Writes);
        var retainedInspection = await campaign.InspectPermanentRetentionAsync(collection, key, requestId, Ct, kind);
        Assert.True(retainedInspection["already_retained"]!.GetValue<bool>());
        Assert.Equal(hash, retainedInspection["evidence_hash"]!.ToString());
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", requestId, Ct));
        Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        var restarted = new BenchmarkCampaign(records, "retention");
        Assert.True(JsonNode.DeepEquals(closure, await restarted.ApplyPermanentRetentionAsync(collection, key, requestId, hash, Operator, Authorization, Ct, kind)));
        Assert.Equal(writes + 1, records.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyPermanentRetentionAsync(collection, key, requestId, hash, Operator, "different authority", Ct, kind));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InspectPermanentRetentionAsync(collection, key, requestId, Ct, kind is null ? Http502 : null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyPermanentRetentionAsync(collection, key, requestId, hash, Operator, Authorization, Ct, kind is null ? Http502 : null));
        var calls = 0;
        foreach (var id in new[] { requestId, session + ":2:new", session + "-execution:1:new", session + "-copilot:request" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallAsync(new() { ClientRequestId = id },
                _ => Task.CompletedTask, _ => { calls++; return Task.FromResult(new LLMResponse()); }, Ct, allowHttpRecovery: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(session, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecoverPendingReceiptAsync(session, 1, requestId, 0, "new-source", true, Ct));
        Assert.Equal(0, calls);
        await restarted.CallAsync(new() { ClientRequestId = "independent:1:new" }, _ => Task.CompletedTask,
            _ => { calls++; return Task.FromResult(new LLMResponse()); }, Ct);
        Assert.Equal(1, calls);
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        var denied = new BenchmarkHttpJournal(restarted, "over-budget:1", 100, 20, 49.5m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.SaveAsync(new() { Attempts = [new() { Id = "new" }] }, Ct));
        await new BenchmarkCampaign(records, "another-campaign").RequireRunNotRetainedAsync(session, Ct);
    }

    [Theory]
    [InlineData(Http500)]
    [InlineData(Http502)]
    [InlineData(Http504)]
    public async Task HttpRequiresItsExplicitModeAndCannotUseHistoricalClosureOperators(string kind)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainSchemaRejectionAsync(RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind == Http500 ? Http502 : Http500));
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ApplyPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("single-http-501")]
    [InlineData("single-http-503")]
    [InlineData("timeout")]
    public async Task UnknownRetentionModesRejectBeforeWriting(string kind)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAsync<ArgumentException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind));
        await Assert.ThrowsAsync<ArgumentException>(() => campaign.ApplyPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, new string('0', 64), Operator, Authorization, Ct, kind));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    public static TheoryData<string, string> HttpFailureCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var kind in new[] { Http500, Http502, Http504 })
            {
                data.Add(kind, "status_200");
                data.Add(kind, "status_400");
                data.Add(kind, "status_other");
                data.Add(kind, "status_503");
                if (kind != Http504) data.Add(kind, "status_504");
                data.Add(kind, "failure_status");
                data.Add(kind, "failure_kind");
                data.Add(kind, "failure_count");
                data.Add(kind, "not_exhausted");
                data.Add(kind, "not_retryable");
                data.Add(kind, "attempt_failure");
                data.Add(kind, "missing_body");
                data.Add(kind, "zero_attempts");
                data.Add(kind, "mixed_attempts");
                data.Add(kind, "usage");
                data.Add(kind, "receipt");
                data.Add(kind, "missing_policy");
                data.Add(kind, "policy_hash");
                data.Add(kind, "recovery_enabled");
                data.Add(kind, "retries_enabled");
                data.Add(kind, "multiple_allowed_attempts");
            }
            data.Add(Http504, "status_502");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HttpFailureCases))]
    public async Task HttpRequiresExactFailureAndCapturedSingleAttemptPolicy(string kind, string defect)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", RequestId, Ct))!;
        var attempt = journal["transport"]!["Attempts"]![0]!;
        var policy = journal["effective_transport_policy"]!.AsObject();
        if (defect.StartsWith("status_", StringComparison.Ordinal))
        {
            var status = defect == "status_other" ? (kind == Http500 ? 502 : 500)
                : int.Parse(defect["status_".Length..], System.Globalization.CultureInfo.InvariantCulture);
            attempt["Status"] = status; failure["status_code"] = status;
        }
        switch (defect)
        {
            case "failure_status": failure["status_code"] = 503; break;
            case "failure_kind": failure["kind"] = kind == Http504 ? "ServiceUnavailable" : "Timeout"; break;
            case "failure_count": failure["attempt_count"] = 2; break;
            case "not_exhausted": failure["retry_exhausted"] = false; break;
            case "not_retryable": failure["retryable"] = false; break;
            case "attempt_failure": attempt["Failure"] = "timeout"; break;
            case "missing_body": attempt["Body"] = null; break;
            case "zero_attempts": journal["transport"]!["Attempts"] = new JsonArray(); break;
            case "mixed_attempts":
                journal["transport"]!["Attempts"]!.AsArray().Add(new JsonObject { ["Id"] = "other", ["Failure"] = "timeout" });
                failure["attempt_count"] = 2; break;
            case "usage": journal["usage"] = new JsonObject(); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new(), Ct); break;
            case "missing_policy": journal.Remove("effective_transport_policy"); journal.Remove("effective_transport_policy_hash"); break;
            case "policy_hash": journal["effective_transport_policy_hash"] = new string('0', 64); break;
            case "recovery_enabled": policy["allow_http_recovery"] = true; break;
            case "retries_enabled": policy["disable_transport_retries"] = false; break;
            case "multiple_allowed_attempts": policy["effective_retry_policy"]!["MaxAttempts"] = 2; break;
        }
        if (defect is "recovery_enabled" or "retries_enabled" or "multiple_allowed_attempts")
            journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(policy.ToJsonString());
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, failure, Ct);
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    public static TheoryData<string, string, bool> HttpBodyCases
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var kind in new[] { Http500, Http502, Http504 })
            {
                data.Add(kind, """{"error":{"message":"Unavailable","type":"server_error","code":null,"param":null}}""", true);
                data.Add(kind, ErrorBody, true);
                data.Add(kind, """{"error":{"message":"Unavailable"},"usage":{"input_tokens":1}}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable"},"usage":null}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable","usage":null}}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable"},"id":"response_1"}""", false);
                data.Add(kind, """{"choices":[{"message":{"content":"Completed"}}]}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable"},"output":[]}""", false);
                data.Add(kind, """{"errorCode":"failed","errorMessage":"Unavailable","errorDetail":{"usage":1}}""", false);
                data.Add(kind, """{"errorCode":"failed","errorMessage":"Unavailable","errorDetail":"","usage":null}""", false);
                data.Add(kind, """{"errorCode":"failed","errorMessage":"Unavailable","errorDetail":"","unknown":"value"}""", false);
                data.Add(kind, """{"errorCode":"failed","errorMessage":"Unavailable","errorMessage":"duplicate"}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable","message":"duplicate"}}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable"},"error":{"message":"duplicate"}}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable","extra":null}}""", false);
                data.Add(kind, """{"error":{"message":"Unavailable","code":{"usage":1}}}""", false);
                data.Add(kind, """{"error":{"message":null}}""", false);
                data.Add(kind, "not JSON", false);
                data.Add(kind, "[]", false);
                data.Add(kind, "null", false);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HttpBodyCases))]
    public async Task HttpBodyMustBeBoundedRecognizedErrorOnlyEvidence(string kind, string body, bool eligible)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Attempts"]![0]!["Body"] = body;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var before = records.Snapshot(); var writes = records.Writes;
        if (eligible)
            Assert.NotNull(await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
                SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData(Http500)]
    [InlineData(Http502)]
    [InlineData(Http504)]
    public async Task HttpOversizedErrorBodyIsRefusedWithoutTruncation(string kind)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Attempts"]![0]!["Body"] = new JsonObject
            { ["error"] = new JsonObject { ["message"] = new string('x', 65_536) } }.ToJsonString();
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind));
        Assert.Equal(before, records.Snapshot());
    }

    public static TheoryData<string, string> ChangedHttpCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var kind in new[] { Http500, Http502, Http504 })
            {
                data.Add(kind, "body");
                data.Add(kind, "policy");
                data.Add(kind, "authorization");
                data.Add(kind, "hash");
                data.Add(kind, "mode");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ChangedHttpCases))]
    public async Task ChangedHttpEvidenceOrAuthorityRejectsApplyWithoutWrites(string kind, string change)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        if (change is "body" or "policy")
        {
            var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
            if (change == "body")
                journal["transport"]!["Attempts"]![0]!["Body"] = """{"error":{"message":"Different retained error"}}""";
            else
            {
                var policy = journal["effective_transport_policy"]!.AsObject();
                policy["effective_retry_policy"]!["MaxUncertainRetries"] = 0;
                journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(policy.ToJsonString());
            }
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        }
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAnyAsync<Exception>(() => campaign.ApplyPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
            change == "hash" ? new string('0', 64) : proposal["evidence_hash"]!.ToString(), Operator,
            change == "authorization" ? "" : Authorization, Ct, change == "mode" ? null : kind));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData(Http500)]
    [InlineData(Http502)]
    [InlineData(Http504)]
    public async Task CliHttpInspectionNeedsExplicitModeAndDoesNotCreateALeaseOrClosure(string kind)
    {
        var root = Directory.CreateTempSubdirectory("retention-cli-").FullName;
        try
        {
            var records = KeyVaultBenchmarkModel.WorkspaceRecords(root);
            var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
            string[] args = ["--schema-portability", "retain-uncertain-request", "--workspace", root, "--campaign", "retention",
                "--run-key", "run:" + Session, "--request-id", RequestId];
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaPortabilityCampaign.RunAsync(args));
            await SchemaPortabilityCampaign.RunAsync([.. args, "--retention-kind", kind]);
            Assert.False(File.Exists(Path.Combine(root, ".GnOuGo", "data", "planning-evaluation", "retention.lock")));
            Assert.Null(await campaign.LoadAsync(Closures, RequestId, Ct));
            Assert.Equal(1m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        }
        finally { Directory.Delete(root, true); }
    }

    public static TheoryData<string?, string> IneligibleCases
    {
        get
        {
            var data = new TheoryData<string?, string>();
            foreach (var kind in new string?[] { null, Http500, Http502, Http504 })
            {
                data.Add(kind, "active");
                data.Add(kind, "executed");
                data.Add(kind, "execution_result");
                data.Add(kind, "tool");
                data.Add(kind, "receipt");
                data.Add(kind, "http_success");
                data.Add(kind, "response_body");
                data.Add(kind, "usage");
                data.Add(kind, "missing_failure");
                data.Add(kind, "wrong_count");
                data.Add(kind, "not_exhausted");
                data.Add(kind, "cancelled");
                data.Add(kind, "zero_attempts");
                data.Add(kind, "two_attempts");
                data.Add(kind, "missing_reservation");
                data.Add(kind, "wrong_request");
                data.Add(kind, "wrong_pending");
                data.Add(kind, "wrong_tenant");
                data.Add(kind, "missing_source");
                data.Add(kind, "missing_no_replay_policy");
                data.Add(kind, "replayable_transport");
                data.Add(kind, "version");
                data.Add(kind, "purpose");
                data.Add(kind, "approved");
                data.Add(kind, "yaml");
                data.Add(kind, "input_under_reserved");
                data.Add(kind, "output_under_reserved");
                data.Add(kind, "quote_missing");
                data.Add(kind, "policy_mismatch");
                data.Add(kind, "manifest_mismatch");
                data.Add(kind, "transport_hash");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(IneligibleCases))]
    public async Task IneligibleEvidenceRejectsWithoutWriting(string? kind, string defect)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: kind);
        async Task Change(string collection, string key, Action<JsonObject> change)
        {
            var value = (await campaign.LoadAsync(collection, key, Ct))!; change(value);
            await campaign.SaveAsync(collection, key, value, Ct);
        }
        switch (defect)
        {
            case "active": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["status"] = "generating"); break;
            case "executed": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["execution_started"] = true); break;
            case "execution_result": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["execution"] = new JsonObject()); break;
            case "tool": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["pendingCall"]!["request"]!["tools"] = new JsonArray(new JsonObject())); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new(), Ct); break;
            case "http_success": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["transport"]!["Attempts"]![0]!["Status"] = 200); break;
            case "response_body": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["transport"]!["Attempts"]![0]!["Body"] = "{}"); break;
            case "usage": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["usage"] = new JsonObject()); break;
            case "missing_failure": await campaign.SaveAsync("planning-evaluation-failures", RequestId, new(), Ct); break;
            case "wrong_count": await Change("planning-evaluation-failures", RequestId, r => r["attempt_count"] = 2); break;
            case "not_exhausted": await Change("planning-evaluation-failures", RequestId, r => r["retry_exhausted"] = false); break;
            case "cancelled": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["transport"]!["Attempts"]![0]!["Failure"] = "cancelled"); break;
            case "zero_attempts": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["transport"]!["Attempts"] = new JsonArray()); break;
            case "two_attempts": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["transport"]!["Attempts"]!.AsArray().Add(r["transport"]!["Attempts"]![0]!.DeepClone())); break;
            case "missing_reservation": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r.Remove("cost_ceiling_eur")); break;
            case "wrong_request": await Change("planning-evaluation-requests", RequestId, r => r["prompt"] = "changed"); break;
            case "wrong_pending": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["pendingCall"]!["id"] = "other:1"); break;
            case "wrong_tenant": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["request"]!["tenantId"] = "other"); break;
            case "missing_source": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r.Remove("source")); break;
            case "missing_no_replay_policy": await Change("planning-evaluation-session-policy", Session, r => r.Remove("construction_review_headroom")); break;
            case "replayable_transport": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["effective_transport_policy"]!["effective_retry_policy"]!["MaxAttempts"] = 4); break;
            case "version": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["construction"]!["version"] = 99); break;
            case "purpose": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["pendingCall"]!["purpose"] = "execution"); break;
            case "approved": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["approvedHash"] = "approved"); break;
            case "yaml": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["session"]!["yaml"] = "executable"); break;
            case "input_under_reserved": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["input_ceiling"] = 1); break;
            case "output_under_reserved": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["output_ceiling"] = 1); break;
            case "quote_missing": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r.Remove("exchange_quote")); break;
            case "policy_mismatch": await Change("planning-evaluation-session-policy", Session, r => r["max_physical_attempts"] = 999); break;
            case "manifest_mismatch": await Change(SchemaPortabilityCampaign.Collection, "run:" + Session, r => r["manifest"] = new JsonObject { ["production_sha"] = new string('b', 40) }); break;
            case "transport_hash": await Change(BenchmarkHttpJournal.Collection, RequestId, r => r["effective_transport_policy_hash"] = new string('0', 64)); break;
        }
        var writes = records.Writes; var snapshot = records.Snapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind));
        Assert.Equal(writes, records.Writes); Assert.Equal(snapshot, records.Snapshot());
    }

    public static TheoryData<string?, string> ChangedEvidenceCases
    {
        get
        {
            var data = new TheoryData<string?, string>();
            foreach (var kind in new string?[] { null, Http500, Http502, Http504 })
            {
                data.Add(kind, "run");
                data.Add(kind, "failure");
                data.Add(kind, "accounting");
                data.Add(kind, "authorization");
                data.Add(kind, "hash");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ChangedEvidenceCases))]
    public async Task ChangedEvidenceOrMissingAuthorityRejectsApplyWithoutWrites(string? kind, string change)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: kind is not null, retentionKind: kind);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        if (change is "run" or "failure")
        {
            var collection = change == "run" ? SchemaPortabilityCampaign.Collection : "planning-evaluation-failures";
            var key = change == "run" ? "run:" + Session : RequestId;
            var row = (await campaign.LoadAsync(collection, key, Ct))!; row["additional_evidence"] = true;
            await campaign.SaveAsync(collection, key, row, Ct);
        }
        if (change == "accounting")
            await new BenchmarkHttpJournal(campaign, "other:1", 1, 1, .1m).SaveAsync(new() { Attempts = [new() { Id = "other" }] }, Ct);
        var writes = records.Writes; var snapshot = records.Snapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
            change == "hash" ? new string('0', 64) : proposal["evidence_hash"]!.ToString(), Operator, change == "authorization" ? "" : Authorization, Ct, kind));
        Assert.Equal(writes, records.Writes); Assert.Equal(snapshot, records.Snapshot());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, Http500)]
    [InlineData(false, Http502)]
    [InlineData(false, Http504)]
    [InlineData(true, Http500)]
    [InlineData(true, Http502)]
    [InlineData(true, Http504)]
    public async Task InterruptedClosurePublicationIsIdempotentAndNeverReleasesReservation(bool afterCommit, string? kind)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: kind is not null, retentionKind: kind);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        var hash = proposal["evidence_hash"]!.ToString(); var original = records.Snapshot();
        records.FailClosure = true; records.FailAfterCommit = afterCommit;
        await Assert.ThrowsAsync<IOException>(() => campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind));
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        if (!afterCommit)
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "independent:1" }, _ => Task.CompletedTask,
                _ => throw new Exception("Must not dispatch"), Ct));
        records.FailClosure = false;
        var restarted = new BenchmarkCampaign(records, "retention");
        await restarted.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind);
        Assert.Single(await records.ListAsync(Closures, "benchmark", BenchmarkCampaign.Author, Ct));
        Assert.Equal(1m, (await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Http500)]
    [InlineData(Http502)]
    [InlineData(Http504)]
    public async Task ForgedClosureBindingsFailClosedAndCannotBeOverwritten(string? kind)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: kind is not null, retentionKind: kind);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
        var hash = proposal["evidence_hash"]!.ToString();
        var closure = await campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind);
        closure["evidence"]!["session_id"] = "forged";
        await campaign.SaveAsync(Closures, RequestId, closure, Ct);
        var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RequireRunNotRetainedAsync(Session, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct, kind));
        Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public async Task EffectiveTransportPolicyIsCapturedBeforeAttemptsAndCannotRewriteHistory()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "transport");
        var policy = TransportPolicy();
        var journal = new BenchmarkHttpJournal(campaign, "new:1", 100, 20, 1m, effectiveTransportPolicy: policy);
        await journal.PrepareAsync(Ct);
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "new:1", Ct))!;
        Assert.Empty(before["transport"]!["Attempts"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(policy, before["effective_transport_policy"]));
        policy["effective_retry_policy"]!["MaxAttempts"] = 4;
        await journal.SaveAsync(new() { Attempts = [new() { Id = "new" }] }, Ct);
        Assert.Equal(1, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "new:1", Ct))!["effective_transport_policy"]!["effective_retry_policy"]!["MaxAttempts"]!.GetValue<int>());
        var changed = new BenchmarkHttpJournal(campaign, "new:1", 100, 20, 1m, effectiveTransportPolicy: policy);
        var retainedAttempts = (await journal.LoadAsync(Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.SaveAsync(retainedAttempts, Ct));
        var historical = new BenchmarkHttpJournal(campaign, "old:1", 100, 20, 1m);
        await historical.PrepareAsync(Ct);
        var retrofit = new BenchmarkHttpJournal(campaign, "old:1", 100, 20, 1m, effectiveTransportPolicy: TransportPolicy());
        await Assert.ThrowsAsync<InvalidOperationException>(() => retrofit.SaveAsync(new(), Ct));
        await historical.SaveAsync(new(), Ct);
        Assert.False((await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "old:1", Ct))!.ContainsKey("effective_transport_policy"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Http500)]
    [InlineData(Http502)]
    [InlineData(Http504)]
    public async Task PermanentRetentionSurvivesEncryptedStorageAndTenantBoundaries(string? kind)
    {
        var root = Directory.CreateTempSubdirectory("retention-encrypted-").FullName;
        try
        {
            var path = Path.Combine(root, "vault.db"); var store = new KeyVaultRecordStore(path);
            var campaign = await SeedAsync(store, capturedTransport: kind is not null, retentionKind: kind);
            var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, kind);
            const string secret = "private-retention-authorization-7e356d";
            await campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId,
                proposal["evidence_hash"]!.ToString(), Operator, secret, Ct, kind);
            var restored = new KeyVaultRecordStore(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkCampaign(restored, "retention").RequireRunNotRetainedAsync(Session, Ct));
            Assert.Null(await restored.GetAsync(Closures, "other-tenant", "retention:" + RequestId, BenchmarkCampaign.Author, Ct));
            foreach (var file in Directory.EnumerateFiles(root))
                Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InterruptedRetentionBindsMissingOutcomeWithoutFabricatingFailureOrResult()
    {
        var records = new Records(); var campaign = await SeedAsync(records, true, true, Interrupted);
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
    [InlineData("construction_intermediate_judge")]
    [InlineData("replan")]
    public async Task InterruptedRetentionAcceptsMatchingPlanningPhaseAndRetainedBackgroundPreference(string purpose)
    {
        var records = new Records(); var campaign = await SeedAsync(records, true, true, Interrupted);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedKey, Ct))!;
        run["session"]!["phase"] = purpose == "replan" ? PlanningPhase.Replanning : purpose;
        run["session"]!["pendingCall"]!["purpose"] = purpose;
        // The benchmark adapter forces synchronous transport on its dispatch copy;
        // the original request's presentation preference remains byte-exact.
        run["session"]!["pendingCall"]!["request"]!["useBackgroundMode"] = true;
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
        var records = new Records(); var campaign = await SeedAsync(records, true, true, Interrupted);
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
            case "recovery": policy["allow_http_recovery"] = true; break;
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
        var records = new Records(); var campaign = await SeedAsync(records, true, true, Interrupted);
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
        var records = new Records(); var campaign = await SeedAsync(records, true, true, Interrupted);
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
            var campaign = await SeedAsync(store, true, true, Interrupted);
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

    private static JsonObject TransportPolicy() => new()
    {
        ["version"] = 1, ["disable_transport_retries"] = true, ["allow_http_recovery"] = false,
        ["effective_retry_policy"] = new JsonObject { ["MaxAttempts"] = 1, ["MaxUncertainRetries"] = 1 }
    };

    private static async Task<BenchmarkCampaign> SeedAsync(IKeyVaultRecordStore records, bool capturedTransport = false, bool corpus = false,
        string? retentionKind = null)
    {
        var campaign = new BenchmarkCampaign(records, "retention");
        var interrupted = retentionKind == Interrupted;
        var session = interrupted ? InterruptedSession : Session;
        var requestId = interrupted ? InterruptedRequest : RequestId;
        var request = new LLMRequest { ClientRequestId = requestId, Prompt = "Original exact intent.", MaxTokens = 8192,
            StructuredOutputSchema = new JsonObject { ["title"] = "construction_intent", ["type"] = "object" } };
        var state = new PlanningSession { Request = new() { TenantId = "benchmark", SessionId = session }, Revision = 1, IntentVersion = 3,
            Status = interrupted ? PlanningStatus.Generating : PlanningStatus.Stopped, ModelCalls = 1, Construction = new() { Version = 2 },
            PendingCall = new() { Id = requestId, Purpose = "construction_intent", Request = request } };
        if (interrupted) { state.Request.Name = "fixture"; state.Phase = "construction_intent"; }
        if (!interrupted) state.Diagnostics.Add(new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Completion remains unknown."));
        await campaign.SaveAsync("planning-evaluation-requests", requestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var journal = new BenchmarkHttpJournal(campaign, requestId, 6651, 8192, 1m, 64, effectiveTransportPolicy: capturedTransport ? TransportPolicy() : null);
        await journal.PrepareAsync(Ct, new JsonObject { ["source_currency"] = "USD", ["target_currency"] = "EUR", ["rate"] = .9m,
            ["source"] = "retained-test-authority", ["as_of_utc"] = DateTimeOffset.UtcNow.AddDays(-1) });
        var transport = new LLMHttpRetryState { Fingerprint = interrupted ? new string('a', 64) : "original-http", Attempts = [new() { Id = requestId + ":attempt",
            Failure = retentionKind is null ? "timeout" : null }] };
        await journal.SaveAsync(transport, Ct);
        if (retentionKind is not null && !interrupted)
        {
            transport.Attempts[0].Status = HttpStatus(retentionKind); transport.Attempts[0].Body = ErrorBody;
            await journal.SaveAsync(transport, Ct);
        }
        var failure = new JsonObject
        {
            ["stage"] = "dispatch", ["kind"] = retentionKind is null or Http504 ? "Timeout" : "ServiceUnavailable",
            ["retryable"] = true, ["attempt_count"] = 1, ["retry_exhausted"] = true
        };
        if (retentionKind is not null && !interrupted) failure["status_code"] = HttpStatus(retentionKind);
        if (!interrupted) await campaign.SaveAsync("planning-evaluation-failures", requestId, failure, Ct);
        await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", new() { ["MaxAttempts"] = 4, ["MaxUncertainRetries"] = 1 }, Ct);
        await campaign.SaveAsync("planning-evaluation-session-policy", session, new() { ["construction_review_headroom"] = 2, ["construction_version"] = 2,
            ["max_physical_attempts"] = state.Request.MaxModelCalls, ["max_input_tokens"] = state.Request.Generation.MaxInputTokensPerRequest,
            ["max_output_tokens"] = state.Request.Generation.MaxOutputTokens, ["max_corrections"] = state.Request.MaxReplanAttempts }, Ct);
        var run = new JsonObject { ["source"] = new string('a', 40), ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession),
            ["result"] = new JsonObject { ["status"] = "stopped", ["execution_status"] = "not_started", ["execution_correct"] = false } };
        if (interrupted)
        {
            run.Remove("source"); run.Remove("result"); run["final_review"] = false;
            await campaign.SaveAsync("planning-evaluation-configuration", InterruptedConfigurationKey,
                PlanningBenchmarkMeasurements.Configuration(state.Request), Ct);
        }
        await campaign.SaveAsync(corpus ? "planning-evaluation-runs" : SchemaPortabilityCampaign.Collection,
            interrupted ? InterruptedKey : corpus ? "source:pilot:case:1" : "run:" + Session, run, Ct);
        return campaign;
    }

    private static int HttpStatus(string kind) => kind switch
    {
        Http500 => 500, Http502 => 502, Http504 => 504,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

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
