using System.Text.Json.Nodes;
using System.Net;
using GnOuGo.AI.Core;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;
public sealed class BenchmarkCampaignTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CapturedRetriesReserveEveryFailedPhysicalAttemptIncludingRateLimits()
    {
        var campaign = new BenchmarkCampaign(new Records(), "confirmed-retries");
        const string id = "session:1:hash";
        var policy = StrictTransport();
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m, 64, .2m, 1, policy);
        var identities = new List<string>();
        using var http = new HttpClient(new Handler(message =>
        {
            identities.Add(message.Headers.GetValues("X-Client-Request-Id").Single());
            return identities.Count < 3
                ? new(identities.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{\"error\":{\"message\":\"temporarily unavailable\",\"code\":\"rate_limit_exceeded\"}}") }
                : new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        using (new LLMHttpRetryContext(id, journal, new() { MaxAttempts = 3, MaxUncertainRetries = 0,
            BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 }).Activate())
            await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1" },
                new() { Prompt = "fixed", MaxOutputTokens = 20 }, Ct);
        var usage = await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, Ct);
        Assert.Equal(3, identities.Distinct().Count());
        Assert.Equal(2, usage["uncertain_attempts"]!.GetValue<int>());
        Assert.Equal(2m, usage["reserved_cost_eur"]!.GetValue<decimal>());
        var record = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var reported = (await LiveCampaignEvidence.AccountingAsync(campaign, "session"))["planning"]!;
        var closed = PlanningBenchmarkMeasurements.ClosedHttpUsage([record], 3)!;
        foreach (var result in new JsonNode[] { accounting, reported, closed })
            Assert.Equal(2m, result["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(.1m, accounting["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(200L, accounting["reserved_input_tokens"]!.GetValue<long>());
        Assert.Equal(40L, accounting["reserved_output_tokens"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(503, 2)]
    [InlineData(200, 1)]
    public async Task CapturedRetryRecoveryUsesOnlyExactCompleteOutcomes(int? interruptedStatus, int expectedSends)
    {
        var campaign = new BenchmarkCampaign(new Records(), "strict-recovery");
        const string id = "session:1:hash";
        var request = new LLMRequest { ClientRequestId = id, Prompt = "same exact prompt" };
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m, 64, effectiveTransportPolicy: StrictTransport());
        var sends = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            sends++;
            return sends == 1 && interruptedStatus == 503
                ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"unavailable\"}}") }
                : new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        async Task<LLMResponse> Dispatch(ILLMHttpRetryJournal active, CancellationToken ct)
        {
            using var retry = new LLMHttpRetryContext(id, active, new() { MaxAttempts = 3, MaxUncertainRetries = 0,
                BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 }).Activate();
            var response = await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1" },
                new() { Prompt = "fixed", MaxOutputTokens = 20 }, ct);
            return new() { Text = response.Text, Json = response.Json, Usage = await journal.CompleteAsync(new()
                { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, ct) };
        }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
            ct => Dispatch(new InterruptingJournal(journal, interruptedStatus), ct), Ct, allowHttpRecovery: true));
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
        if (interruptedStatus is null)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
                ct => Dispatch(journal, ct), Ct, allowHttpRecovery: true));
            Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        }
        else
        {
            var response = await campaign.CallAsync(request, _ => Task.CompletedTask, ct => Dispatch(journal, ct), Ct, allowHttpRecovery: true);
            Assert.Equal("{}", response.Text);
            Assert.Equal(interruptedStatus == 503 ? 1 : 0, response.Usage!["uncertain_attempts"]!.GetValue<int>());
            await campaign.CallAsync(request, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), Ct, allowHttpRecovery: true);
        }
        Assert.Equal(expectedSends, sends);
    }

    private sealed class InterruptingJournal(ILLMHttpRetryJournal inner, int? status) : ILLMHttpRetryJournal
    {
        private bool _failed;
        public Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
        public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
        {
            await inner.SaveAsync(state, ct);
            if (!_failed && state.Attempts.Count > 0 && state.Attempts[^1].Status == status)
            { _failed = true; throw new IOException("Interrupted after durable attempt evidence."); }
        }
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("tokens")]
    [InlineData("time")]
    [InlineData("money")]
    public async Task EveryRetryKeepsRequiredReviewHeadroomAndPriorReservations(string limit)
    {
        var campaign = new BenchmarkCampaign(new Records(), "strict-" + limit);
        var clock = new BenchmarkClock();
        var policy = StrictTransport(clock.GetUtcNow().AddSeconds(1));
        policy["max_total_tokens"] = limit == "tokens" ? 350L : 15_000_000L;
        policy["review_token_headroom"] = 120L;
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, limit == "money" ? 24m : .1m,
            limit == "calls" ? 2 : 64, limit == "money" ? 3m : .1m, 1, policy, clock);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "session:1:hash:1" }] };
        await journal.SaveAsync(state, Ct);
        state.Attempts[0].Status = 429;
        await journal.SaveAsync(state, Ct);
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct))!.ToJsonString();
        if (limit == "time") clock.Advance();
        state.Attempts.Add(new() { Id = "session:1:hash:2" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct))!.ToJsonString());
        Assert.Single((await journal.LoadAsync(Ct))!.Attempts);
    }

    private static JsonObject StrictTransport(DateTimeOffset? deadline = null) => new()
    {
        ["version"] = 2, ["allow_http_recovery"] = true, ["disable_transport_retries"] = false,
        ["effective_retry_policy"] = new JsonObject { ["MaxAttempts"] = 3 },
        ["max_total_tokens"] = 15_000_000L, ["review_token_headroom"] = 120L,
        ["deadline_utc"] = (deadline ?? DateTimeOffset.UtcNow.AddHours(5)).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed class BenchmarkClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance() => _now = _now.AddSeconds(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("No structured completion.")]
    [InlineData("{\"partial\":")]
    public void ReceiptInspectionRetainsEmptyAndNonJsonTextWithoutChangingEvidence(string text)
    {
        var receipt = new JsonObject { ["json"] = null, ["text"] = text, ["completionStatus"] = "output_limit",
            ["usage"] = new JsonObject { ["output_tokens"] = 16384, ["reasoning_tokens"] = 16384 } };
        var original = receipt.ToJsonString();
        var response = Assert.IsAssignableFrom<JsonValue>(SchemaPortabilityCampaign.InspectResponse(receipt));
        Assert.Equal(text, response.GetValue<string>());
        Assert.Null(response.Parent);
        Assert.Equal(original, receipt.ToJsonString());
    }

    [Theory]
    [InlineData("{\"answer\":42}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData(null)]
    public void ReceiptInspectionPreservesExistingJsonAndAbsentResponsePresentation(string? text)
    {
        var receipt = new JsonObject { ["json"] = null, ["text"] = text };
        var original = receipt.ToJsonString();
        Assert.True(JsonNode.DeepEquals(text is null ? null : JsonNode.Parse(text), SchemaPortabilityCampaign.InspectResponse(receipt)));
        Assert.Equal(original, receipt.ToJsonString());
    }

    [Fact]
    public void ReceiptInspectionPrefersStructuredResponseAndReturnsAnIndependentProjection()
    {
        var receipt = new JsonObject { ["json"] = new JsonObject { ["answer"] = 42 }, ["text"] = "not JSON" };
        var original = receipt.ToJsonString();
        var response = Assert.IsType<JsonObject>(SchemaPortabilityCampaign.InspectResponse(receipt));
        Assert.Equal(42, response["answer"]!.GetValue<int>());
        response["answer"] = 0;
        Assert.Equal(original, receipt.ToJsonString());
    }

    [Fact]
    public async Task CompactTransportFreezesPhaseCeilingsAndCannotFallBackToGeneralAllowance()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "compact-policy");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "compact", "amazon", "Requested work", "provider", "model");
        Assert.Equal(64, request.MaxModelCalls);
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "compact", Ct))!;
        Assert.Equal(2, captured["construction_version"]!.GetValue<int>());
        foreach (var (phase, limit) in new[] { ("construction_select", 6000), ("construction_draft", 16000), ("construction_local_judge", 8000) })
            Assert.Equal(limit, KeyVaultBenchmarkModel.PlanningInputLimit(captured, new() { StructuredOutputSchema = new JsonObject { ["title"] = phase } }));
        Assert.Throws<PlanningConflictException>(() => KeyVaultBenchmarkModel.PlanningInputLimit(captured, new()));
        request.Options["construction_input_limits"]!["draft"] = 7000;
        await Assert.ThrowsAsync<PlanningConflictException>(() => model.CapturePlanningPolicyAsync(request, Ct));
        Assert.Equal(16000, (await campaign.LoadAsync("planning-evaluation-session-policy", "compact", Ct))!["construction_input_limits"]!["draft"]!.GetValue<int>());
        Assert.Equal(32000, KeyVaultBenchmarkModel.PlanningInputLimit(new JsonObject { ["max_input_tokens"] = 32000 }, new()));
    }

    [Fact]
    public async Task DraftHeadroomPreservesMandatoryReviewsWithoutInventingChargesOrResettingUnknowns()
    {
        var campaign = new BenchmarkCampaign(new Records(), "review-headroom");
        await new BenchmarkHttpJournal(campaign, "old:1:unknown", 100, 20, 45m)
            .SaveAsync(new() { Attempts = [new() { Id = "old" }] }, Ct);
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var draft = new BenchmarkHttpJournal(campaign, "fresh:1:draft", 100, 20, 3m,
            reviewCostHeadroom: 3m, reviewAttemptHeadroom: 2);
        await draft.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => draft.SaveAsync(new() { Attempts = [new() { Id = "draft" }] }, Ct));
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.Empty((await draft.LoadAsync(Ct))!.Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkHttpJournal(campaign, "fresh:1:draft", 100, 20, 3m)
            .SaveAsync(new(), Ct));
        var allowed = new BenchmarkHttpJournal(campaign, "fresh:2:smaller", 100, 20, 1m,
            reviewCostHeadroom: 3m, reviewAttemptHeadroom: 2);
        await allowed.SaveAsync(new() { Attempts = [new() { Id = "allowed" }] }, Ct);
        var after = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(46m, after["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(2L, after["unknown_attempts"]!.GetValue<long>());
        var exhausted = new BenchmarkHttpJournal(campaign, "bounded:1:draft", 100, 20, .1m,
            sessionAttemptLimit: 2, reviewCostHeadroom: .1m, reviewAttemptHeadroom: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exhausted.SaveAsync(new() { Attempts = [new() { Id = "no-review-room" }] }, Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("legacy")]
    [InlineData("limit")]
    [InlineData("transport")]
    [InlineData("unbounded")]
    [InlineData("excess")]
    [InlineData("runtime")]
    public async Task SourceWindowHeadroomRequiresExactCapturedSessionPolicy(string defect)
    {
        var campaign = new BenchmarkCampaign(new Records(), "window-headroom-" + defect);
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "window", "amazon", "Requested work", "provider", "model");
        request.Options["llm_budget"]!["max_calls"] = 5;
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "window", Ct))!;
        var policy = StrictTransport();
        policy["planning_transport_policy"] = captured[PlanningTransportPolicies.OptionName]!.DeepClone();
        if (defect == "legacy") captured["construction_presentation_version"] = 5;
        if (defect == "limit") captured["max_physical_attempts"] = 4;
        if (defect == "transport") policy["planning_transport_policy"] = new JsonObject();
        if (defect == "runtime") policy["mode"] = "runtime_single_attempt";
        await campaign.SaveAsync("planning-evaluation-session-policy", "window", captured, Ct);
        var id = defect == "missing" ? "absent:1:hash" : "window:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, .1m,
            sessionAttemptLimit: defect == "unbounded" ? null : 5,
            reviewAttemptHeadroom: defect == "excess" ? 6 : 3, effectiveTransportPolicy: policy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(Ct));
        Assert.Null(await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct));
        Assert.Equal(0L, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["calls"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task SourceWindowHeadroomResumesExactlyAndPreservesRequiredCalls(int presentation)
    {
        var campaign = new BenchmarkCampaign(new Records(), "window-headroom-recovery");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "window", "amazon", "Requested work", "provider", "model");
        request.Options["construction_presentation_version"] = presentation;
        request.Options["llm_budget"]!["max_calls"] = 5;
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "window", Ct))!;
        Assert.Equal(presentation, captured["construction_presentation_version"]!.GetValue<int>());
        var policy = StrictTransport();
        policy["planning_transport_policy"] = captured[PlanningTransportPolicies.OptionName]!.DeepClone();
        const string id = "window:1:hash";
        BenchmarkHttpJournal Journal(int reviews = 4, int limit = 5) => new(campaign, id, 100, 20, .1m,
            limit, .4m, reviews, policy);
        await Journal().PrepareAsync(Ct);
        var empty = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
        await Journal().SaveAsync(new(), Ct);
        Assert.Equal(empty, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal(3).SaveAsync(new(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal(limit: 6).SaveAsync(new(), Ct));
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "first", Status = null }] };
        await Journal().SaveAsync(state, Ct);
        state.Attempts[0].Status = 500;
        await Journal().SaveAsync(state, Ct);
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
        state.Attempts.Add(new() { Id = "no-room-for-four-reviews" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal().SaveAsync(state, Ct));
        Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        Assert.Single((await Journal().LoadAsync(Ct))!.Attempts);
        captured["construction_presentation_version"] = 5;
        await campaign.SaveAsync("planning-evaluation-session-policy", "window", captured, Ct);
        state.Attempts.RemoveAt(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal().SaveAsync(state, Ct));
        Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
    }

    [Fact]
    public async Task LargeSessionTransportPolicyIsCapturedOnceAndCannotRaiseHistoricalLimits()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "capacity");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest(["--construction-preset", "large"], "new-large", "amazon", "Requested work", "provider", "model");
        Assert.Equal(384, request.MaxModelCalls); Assert.Equal(64, request.MaxReplanAttempts);
        Assert.Equal(32_000, request.Generation.MaxInputTokensPerRequest);
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "new-large", Ct))!;
        Assert.Equal(384, captured["max_physical_attempts"]!.GetValue<int>());
        var writes = records.Writes;
        await model.CapturePlanningPolicyAsync(request, Ct); Assert.Equal(writes, records.Writes);
        request.MaxModelCalls++;
        await Assert.ThrowsAsync<PlanningConflictException>(() => model.CapturePlanningPolicyAsync(request, Ct));
        Assert.True(JsonNode.DeepEquals(captured, await campaign.LoadAsync("planning-evaluation-session-policy", "new-large", Ct)));
        request.SessionId = "historical";
        await campaign.SaveAsync("planning-evaluation-requests", "historical:1:issued", new JsonObject(), Ct);
        writes = records.Writes;
        await model.CapturePlanningPolicyAsync(request, Ct);
        Assert.Null(await campaign.LoadAsync("planning-evaluation-session-policy", "historical", Ct));
        Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public async Task LiveCohortJudgeSettingIsFrozenWithoutMigratingDisabledManifests()
    {
        var records = new Records();
        var historical = new BenchmarkCampaign(records, "judge-control");
        var original = await LiveCampaignEvidence.PinAsync(historical, "frozen", "provider", "model", "configuration", "cohort");
        Assert.False(original.ContainsKey("business_judge"));
        var serialized = original.ToJsonString(); var writes = records.Writes;
        var continued = await LiveCampaignEvidence.PinAsync(historical, "frozen", "provider", "model", "configuration", "cohort", businessJudge: false);
        Assert.Equal(serialized, continued.ToJsonString());
        Assert.Equal(writes, records.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveCampaignEvidence.PinAsync(historical,
            "frozen", "provider", "model", "configuration", "cohort", businessJudge: true));
        Assert.Equal(serialized, (await historical.LoadAsync(SchemaPortabilityCampaign.Collection, "cohort-manifest", Ct))!.ToJsonString());

        var enabled = new BenchmarkCampaign(records, "judge-enabled");
        var manifest = await LiveCampaignEvidence.PinAsync(enabled, "frozen", "provider", "model", "configuration", "cohort", businessJudge: true);
        Assert.True(manifest["business_judge"]!.GetValue<bool>());
        Assert.Equal(8, manifest["planning_attempts"]!.GetValue<int>());
        Assert.Equal(2, manifest["repairs"]!.GetValue<int>());
        Assert.Equal(96000, manifest["max_input_tokens"]!.GetValue<int>());
        Assert.Equal(32768, manifest["max_output_tokens"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(manifest, await LiveCampaignEvidence.PinAsync(enabled,
            "frozen", "provider", "model", "configuration", "cohort", businessJudge: true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveCampaignEvidence.PinAsync(enabled,
            "frozen", "provider", "model", "configuration", "cohort"));
        manifest.Remove("business_judge");
        Assert.True(JsonNode.DeepEquals(original, manifest));
    }

    [Fact]
    public async Task ExplicitBudgetExtensionPreservesReservationsConfigurationAndIsolationAcrossRestart()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "extension");
        var configuration = new JsonObject { ["cost_ceiling_eur"] = 50 };
        await campaign.PinAsync(configuration, Ct);
        var unknown = new BenchmarkHttpJournal(campaign, "unknown:1:hash", 100, 20, 49m);
        await unknown.SaveAsync(new() { Attempts = [new() { Id = "unknown" }] }, Ct);
        var request = new BenchmarkHttpJournal(campaign, "fresh:1:hash", 100, 20, 2m);
        var attempt = new LLMHttpRetryState { Attempts = [new() { Id = "fresh" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.SaveAsync(attempt, Ct));
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var authorization = await campaign.ExtendBudgetAsync(50m, 100m, "Explicit test operator authorization", Ct);
        var restarted = new BenchmarkCampaign(records, "extension");
        Assert.Equal(100m, await restarted.CostCeilingAsync(Ct));
        Assert.Equal(50m, await new BenchmarkCampaign(records, "other").CostCeilingAsync(Ct));
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        await restarted.PinAsync(configuration, Ct);
        Assert.Single(authorization["history"]!.AsArray());
        await request.SaveAsync(attempt, Ct);
        var totals = await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct);
        Assert.Equal(51m, totals["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(2L, totals["unknown_attempts"]!.GetValue<long>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkHttpJournal(restarted, "over:1:hash", 100, 20, 50m)
            .SaveAsync(new() { Attempts = [new() { Id = "over" }] }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExtendBudgetAsync(50m, 100m, "Stale approval", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExtendBudgetAsync(100m, 100m, "No increase", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.ExtendBudgetAsync(100m, 150m, " ", Ct));
        Assert.True(JsonNode.DeepEquals(authorization, await restarted.LoadAsync("planning-evaluation-configuration", "spending-authorization", Ct)));
    }

    [Fact]
    public void DurableMappingIdentitiesRemainStableAndBelongToExecutionAccounting()
    {
        var original = new LLMRequest { ClientRequestId = "durable-mapping-receipt", Prompt = "Observed data", MaxTokens = 8192 };
        var dispatched = LiveWorkflowEvaluation.ExecutionRequest(original, "cohort-case-1", 1);
        Assert.StartsWith("cohort-case-1-execution:", dispatched.ClientRequestId);
        Assert.Equal(dispatched.ClientRequestId, LiveWorkflowEvaluation.ExecutionRequest(original, "cohort-case-1", 99).ClientRequestId);
        Assert.NotEqual(dispatched.ClientRequestId, LiveWorkflowEvaluation.ExecutionRequest(original, "cohort-case-2", 1).ClientRequestId);
        Assert.Equal("durable-mapping-receipt", original.ClientRequestId);
        Assert.Equal(8192, dispatched.MaxTokens);
    }

    [Fact]
    public async Task LiveReportsRejectMissingManifestsAndRetainInterruptedRuns()
    {
        var campaign = new BenchmarkCampaign(new Records(), "interrupted-live");
        var run = new JsonObject { ["phase"] = "final" };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:final-amazon-1", run, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveCampaignEvidence.ReportAsync(campaign));
        var manifest = new JsonObject { ["production_sha"] = "frozen" };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "final-manifest", manifest, Ct);
        run["manifest"] = manifest.DeepClone();
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:final-amazon-1", run, Ct);
        var report = await LiveCampaignEvidence.ReportAsync(campaign);
        Assert.Equal("interrupted", report["runs"]![0]!["status"]!.ToString());
        Assert.Equal(6, report["runs"]!.AsArray().Count);
        Assert.Equal(0, report["passed"]!.GetValue<int>());
        run["result"] = new JsonObject { ["status"] = "final_review", ["execution_oracle"] = true };
        run["execution_started"] = "retained-before-dispatch";
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:final-amazon-1", run, Ct);
        report = await LiveCampaignEvidence.ReportAsync(campaign);
        Assert.Equal("interrupted", report["runs"]![0]!["execution_status"]!.ToString());
        Assert.Equal(0, report["passed"]!.GetValue<int>());
    }

    [Fact]
    public void ExecutionInputLimitCountsTokensInsteadOfJsonBytes()
    {
        var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = string.Concat(Enumerable.Repeat("A local repository file.\n", 6000)) }) }.ToJsonString();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) > 96000);
        Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(body), 4097, 96000);
        Assert.True(KeyVaultBenchmarkModel.ExecutionInputEstimate(string.Concat(Enumerable.Repeat(body, 10))) > 96000);
    }
    [Fact]
    public void OrdinaryRuntimeInferenceUsesTheSameCampaignInputAllowanceAsTheProxy()
    {
        var request = new LLMRequest { Prompt = string.Concat(Enumerable.Repeat("<span data-item=\"observed\">An observed product</span>", 30000)), MaxTokens = 8192 };
        var snapshot = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        Assert.True(KeyVaultBenchmarkModel.ExecutionInputEstimate(snapshot) > 96000);
        var error = Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() =>
            KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true));
        Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded, error.Code);
        Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        Assert.Equal("request_input_allowance", error.Details["classification"]!.ToString());
        Assert.Equal("input_tokens", error.Details["limit_kind"]!.ToString());
        Assert.Equal("conservative_token_estimate", error.Details["measurement"]!.ToString());
        Assert.Equal(96000, error.Details["limit"]!.GetValue<int>());
        var dispatched = JsonSerializer.Deserialize(snapshot, PlanningJsonContext.Default.LLMRequest)!;
        dispatched.Provider = "pinned"; dispatched.Model = "pinned"; dispatched.UseBackgroundMode = false;
        Assert.Equal(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest)),
            error.Details["current"]!.GetValue<int>());
        Assert.DoesNotContain("observed product", error.Details.ToJsonString());
        Assert.Equal(snapshot, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        request.Prompt = "Observed input";
        Assert.Equal(8192, KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true).MaxTokens);
        request.MaxTokens = 32769;
        Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() =>
            KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CampaignAdmissionFailureAllowsDurableCleanupWithoutInference(bool mapping)
    {
        var definition = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: model
                    type: llm.call
                    input: {model: pinned, prompt: "${data.inputs.observation}", max_tokens: 8192}
                finally:
                  - {id: cleanup, type: set, input: {value: cleaned}}
            """);
        JsonNode observation = JsonValue.Create(string.Concat(Enumerable.Repeat("<span>A</span>", 60000)))!;
        if (mapping)
        {
            var step = definition.Workflows["main"].Steps[0]; step.Type = "mapping.dynamic";
            step.Input = new JsonObject { ["sources"] = new JsonObject { ["snapshot"] = "${data.inputs.observation}" },
                ["objective"] = "Extract the first ten observed entries", ["binding"] = "facts", ["producer_contract"] = "fixture" };
            step.OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"},"maxItems":10}},"required":["items"]}},"required":["value"]}""")!.AsObject();
            observation = new JsonObject { ["pages"] = new JsonArray(Enumerable.Range(0, 53).Select(page => (JsonNode)new JsonObject
            {
                ["records"] = new JsonArray(Enumerable.Range(0, page == 52 ? 21 : 30).Select(index => (JsonNode)new JsonObject
                { ["text"] = "observed/" + page + "/" + index, ["metadata"] = string.Concat(Enumerable.Repeat("<span>Original</span>", 100)) }).ToArray())
            }).ToArray()) };
        }
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(definition);
        var store = new InMemoryWorkflowRunStore(); var client = new AdmissionClient();
        var engine = new WorkflowEngine { LLMClient = client, LlmDefaults = new() { Model = "pinned" }, RunStore = store,
            Limits = new() { TenantId = "tenant", RunId = "run", MaxMappingInputTokens = 96000 } };
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!],
            new JsonObject { ["observation"] = observation }, Ct);
        Assert.Equal("LLM_BUDGET_EXCEEDED", result.Error!.Code);
        Assert.Equal("request_input_allowance", result.Error.Details!["classification"]!.ToString());
        Assert.Equal("model", result.Error.Details["failed_step_id"]!.ToString());
        Assert.Equal(0, client.Dispatches);
        var run = (await store.ReadAsync("tenant", "run", Ct))!;
        Assert.Equal("failed", run.Invocations["/workflow/main/step/model"].Status);
        var receipt = run.Invocations["/workflow/main/step/model"];
        if (mapping)
        {
            Assert.False(receipt.ExternalCompletionObserved); // Admission failed before any model child existed.
            Assert.NotNull(receipt.CompletedAt);
            Assert.Equal("request_input_allowance", receipt.Error!.Details!["classification"]!.ToString());
            Assert.DoesNotContain("/workflow/main/step/model/mapping/0", run.Invocations.Keys);
            Assert.Equal(96000, result.Error.Details["limit"]!.GetValue<int>());
            Assert.Equal("not_started", result.Error.Details["dispatch_status"]!.ToString());
        }
        else Assert.True(receipt.ExternalCompletionObserved);
        Assert.Equal("completed", Assert.Single(run.Invocations.Values, i => i.IsFinalization && i.StepType == "set").Status);
        var originalError = result.Error.Details.ToJsonString();
        var recovered = await new WorkflowEngine { RunStore = store, LLMClient = client }
            .ResumeAsync("tenant", "run", run.Revision, document.Workflows[document.Entrypoint!], Ct);
        Assert.False(recovered.Success);
        Assert.Equal(originalError, recovered.Error!.Details!.ToJsonString());
        Assert.Equal(0, client.Dispatches);
    }
    private sealed class AdmissionClient : ILLMClient, ILLMCapabilityResolver
    {
        public int Dispatches;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(96000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            _ = KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true);
            Dispatches++; return Task.FromResult(new LLMResponse());
        }
    }

    [Fact]
    public async Task ExecutionHasItsOwnAttemptPolicyAndStillSharesTheSpendingCeiling()
    {
        var campaign = new BenchmarkCampaign(new Records(), "execution-limit");
        var journal = new BenchmarkHttpJournal(campaign, "execution:1:hash", 100, 20, 5m, sessionAttemptLimit: null);
        var state = new LLMHttpRetryState();
        for (var i = 0; i < 10; i++) { state.Attempts.Add(new() { Id = i.ToString() }); await journal.SaveAsync(state, Ct); }
        Assert.Equal(10, (await journal.LoadAsync(Ct))!.Attempts.Count);
        Assert.Equal(50m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["cost_upper_bound_eur"]!.GetValue<decimal>());
        state.Attempts.Add(new() { Id = "over-budget" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Equal(10, (await journal.LoadAsync(Ct))!.Attempts.Count);
        var old = new BenchmarkHttpJournal(campaign, "planning:1:hash", 100, 20, .01m);
        await old.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkHttpJournal(campaign, "planning:1:hash", 100, 20, .01m, sessionAttemptLimit: null).SaveAsync(new(), Ct));
    }
    [Fact]
    public async Task FailedPreflightRetainsEvidenceWithoutInventingDispatch()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "preflight");
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "one:1:hash" },
            _ => throw new InvalidOperationException("No currency quote."), _ => throw new Exception("Must not dispatch"), Ct));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-requests", "one:1:hash", Ct));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", "one:1:hash", Ct))!;
        Assert.Equal("preflight", failure["stage"]!.ToString()); Assert.Equal("currency_quote_unavailable", failure["reason"]!.ToString());
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
        await new BenchmarkCampaign(records, "preflight").CallAsync(new() { ClientRequestId = "two:1:hash" }, _ => Task.CompletedTask,
            _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-failures", "one:1:hash", Ct));
    }
    [Fact]
    public async Task LiveCohortKeepsExecutionAccountingSeparateAndCountsMissingRuns()
    {
        var campaign = new BenchmarkCampaign(new Records(), "live-accounting");
        async Task Record(string id, int? status, bool usage)
        {
            var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
            var state = new LLMHttpRetryState { Attempts = [new() { Id = id }] };
            await journal.SaveAsync(state, Ct); state.Attempts[0].Status = status; await journal.SaveAsync(state, Ct);
            if (usage) await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, Ct);
        }
        await Record("final-amazon-1:1:hash", 200, true);
        await Record("final-amazon-1-execution:1:hash", null, false);
        await Record("diagnostic-other:1:hash", 200, true);
        var accounting = await LiveCampaignEvidence.AccountingAsync(campaign, "final-amazon-1");
        Assert.Equal(1L, accounting["planning"]!["physical_attempts"]!.GetValue<long>());
        Assert.Equal(10L, accounting["planning"]!["known_input_tokens"]!.GetValue<long>());
        Assert.Equal(1L, accounting["execution"]!["unknown_attempts"]!.GetValue<long>());
        Assert.Equal(1m, accounting["execution"]!["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(3L, accounting["campaign"]!["calls"]!.GetValue<long>());
        var report = await LiveCampaignEvidence.ReportAsync(campaign);
        Assert.Equal(6, report["runs"]!.AsArray().Count); Assert.False(report["complete"]!.GetValue<bool>());
        var manifest = new JsonObject { ["production_sha"] = "one" };
        LiveCampaignEvidence.RequireMatch(manifest, manifest.DeepClone().AsObject());
        Assert.Throws<InvalidOperationException>(() => LiveCampaignEvidence.RequireMatch(manifest, new() { ["production_sha"] = "two" }));
    }
    [Theory]
    [InlineData("/v1/responses", "max_output_tokens")]
    [InlineData("/v1/chat/completions", "max_completion_tokens")]
    public void ExecutionInferenceUsesBoundedVisibleInputAndPinnedReasoning(string path, string field)
    {
        var original = JsonNode.Parse("""{"model":"pinned","messages":[{"role":"user","content":"Review"}],"max_tokens":65536,"stream":true,"reasoning_effort":"high"}""")!.AsObject();
        var before = original.ToJsonString(); var prepared = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", path);
        Assert.Equal(before, original.ToJsonString()); Assert.Equal(32768, prepared[field]!.GetValue<int>()); Assert.Null(prepared["max_tokens"]);
        Assert.Equal("medium", (path.EndsWith("responses", StringComparison.Ordinal) ? prepared["reasoning"]!["effort"] : prepared["reasoning_effort"])!.ToString());
        foreach (var invalid in new[] { "{\"model\":\"other\"}", "{\"model\":\"pinned\",\"n\":2}", "{\"model\":\"pinned\",\"conversation\":\"hidden\"}", "{\"model\":\"pinned\",\"messages\":[{\"type\":\"input_image\"}]}" })
            Assert.Throws<InvalidOperationException>(() => KeyVaultBenchmarkModel.PrepareProxyPayload(JsonNode.Parse(invalid)!.AsObject(), "pinned", path));
    }
    [Theory]
    [InlineData(400, true)]
    [InlineData(200, false)]
    [InlineData(null, false)]
    public async Task OnlyVerifiedSchemaRejectionsCanCloseWithoutInventingCompletion(int? status, bool canClose)
    {
        var campaign = new BenchmarkCampaign(new Records(), "schema-rejection"); const string id = "session:1:hash";
        await campaign.SaveAsync("planning-evaluation-requests", id, new() { ["clientRequestId"] = id }, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", id, new() { ["status_code"] = 400, ["safe_provider_code"] = "invalid_json_schema" }, Ct);
        var journal = new BenchmarkHttpJournal(campaign, id, 96000, 32768, 1m);
        var transport = new LLMHttpRetryState { Attempts = [new() { Id = "one" }] };
        await journal.SaveAsync(transport, Ct); transport.Attempts[0].Status = status; await journal.SaveAsync(transport, Ct);
        if (!canClose)
        { await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainSchemaRejectionAsync(id, Ct)); return; }
        await campaign.RetainSchemaRejectionAsync(id, Ct);
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = id }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct));
        await campaign.CallAsync(new() { ClientRequestId = "next:1:hash" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Equal(1, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["calls"]!.GetValue<long>());
    }
    [Theory]
    [InlineData("exact", true)]
    [InlineData("other_schema_constraint", true)]
    [InlineData("ordinary400", false)]
    [InlineData("malformed_body", false)]
    [InlineData("nonobject_body", false)]
    [InlineData("missing_body", false)]
    [InlineData("wrong_param", false)]
    [InlineData("wrong_type", false)]
    [InlineData("wrong_format_name", false)]
    [InlineData("non_null_code", false)]
    [InlineData("missing_code", false)]
    [InlineData("empty_schema_detail", false)]
    [InlineData("success", false)]
    [InlineData("unknown", false)]
    [InlineData("usage", false)]
    [InlineData("receipt", false)]
    [InlineData("retryable", false)]
    [InlineData("wrong_count", false)]
    [InlineData("wrong_stage", false)]
    [InlineData("wrong_failure_kind", false)]
    [InlineData("wrong_request", false)]
    [InlineData("unstructured", false)]
    public async Task CodeLessSchemaRejectionRequiresTheExactOriginalTransportEnvelope(string evidence, bool canClose)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "code-less-schema");
        const string id = "session:2:hash";
        var request = new JsonObject { ["clientRequestId"] = evidence == "wrong_request" ? "other:2:hash" : id,
            ["structuredOutputStrict"] = true, ["structuredOutputSchema"] = new JsonObject { ["type"] = "object" } };
        if (evidence == "unstructured") request.Remove("structuredOutputSchema");
        var failure = new JsonObject { ["stage"] = evidence == "wrong_stage" ? "preflight" : "dispatch",
            ["kind"] = evidence == "wrong_failure_kind" ? "Transport" : "InvalidRequest", ["status_code"] = 400,
            ["safe_provider_code"] = null, ["attempt_count"] = evidence == "wrong_count" ? 2 : 1,
            ["retryable"] = evidence == "retryable" };
        var error = new JsonObject
        {
            ["type"] = evidence == "wrong_type" ? "server_error" : "invalid_request_error",
            ["param"] = evidence == "wrong_param" ? "messages" : "response_format",
            ["code"] = evidence == "non_null_code" ? "another_error" : null,
            ["message"] = evidence switch
            {
                "ordinary400" => "Invalid value for response_format.",
                "wrong_format_name" => "Invalid schema for response_format 'unrelated': invalid schema.",
                "empty_schema_detail" => "Invalid schema for response_format 'output': ",
                "other_schema_constraint" => "Invalid schema for response_format 'output': required must list every declared property.",
                _ => "Invalid schema for response_format 'output': context=('anyof', '2', 'properties', 'minItems'), $ref cannot have keywords {'description'}."
            }
        };
        if (evidence == "missing_code") error.Remove("code");
        var body = evidence switch { "malformed_body" => "{", "nonobject_body" => "[]", "missing_body" => null,
            _ => new JsonObject { ["error"] = error }.ToJsonString() };
        await campaign.SaveAsync("planning-evaluation-requests", id, request, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", id, failure, Ct);
        var journal = new BenchmarkHttpJournal(campaign, id, 3000, 8192, 1m);
        var transport = new LLMHttpRetryState { Attempts = [new() { Id = "one" }] };
        await journal.SaveAsync(transport, Ct);
        transport.Attempts[0].Status = evidence switch { "success" => 200, "unknown" => null, _ => 400 };
        transport.Attempts[0].Body = body;
        await journal.SaveAsync(transport, Ct);
        if (evidence == "usage")
        {
            var row = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            row["usage"] = new JsonObject(); await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, row, Ct);
        }
        if (evidence == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct);
        var collections = new[] { "planning-evaluation-requests", "planning-evaluation-failures", BenchmarkHttpJournal.Collection,
            "planning-evaluation-receipts" };
        var originals = new Dictionary<string, string?>();
        foreach (var collection in collections) originals[collection] = (await campaign.LoadAsync(collection, id, Ct))?.ToJsonString();
        var writes = records.Writes;
        if (!canClose)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainSchemaRejectionAsync(id, Ct));
            Assert.Null(await campaign.LoadAsync("planning-evaluation-closures", id, Ct));
            Assert.Equal(writes, records.Writes);
        }
        else
        {
            var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
            await campaign.RetainSchemaRejectionAsync(id, Ct);
            var closure = (await campaign.LoadAsync("planning-evaluation-closures", id, Ct))!;
            Assert.Equal("provider_schema_rejected", closure["reason"]!.ToString());
            Assert.Equal("retained_response_format_schema_error", closure["verification"]!.ToString());
            Assert.Equal(writes + 1, records.Writes);
            await campaign.RetainSchemaRejectionAsync(id, Ct);
            Assert.Equal(writes + 1, records.Writes);
            Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", id, Ct));
            Assert.Null((await campaign.LoadAsync("planning-evaluation-failures", id, Ct))!["safe_provider_code"]);
            Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = id },
                _ => Task.CompletedTask, _ => throw new Exception("A rejected request must never dispatch again."), Ct));
            await campaign.CallAsync(new() { ClientRequestId = "fresh:1:hash" }, _ => Task.CompletedTask,
                _ => Task.FromResult(new LLMResponse()), Ct);
        }
        foreach (var collection in collections)
            Assert.Equal(originals[collection], (await campaign.LoadAsync(collection, id, Ct))?.ToJsonString());
    }
    [Fact]
    public void LiveAdapterUsesSynchronousRecoveryWithoutChangingReservedRequest()
    {
        var request = new LLMRequest
        {
            ClientRequestId = "session:1:hash", Prompt = "business request", UseBackgroundMode = true,
            DisableTransportRetries = true, MaxTokens = 32768,
            StructuredOutputSchema = new JsonObject { ["type"] = "object" }, StructuredOutputStrict = true
        };
        var original = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var dispatched = KeyVaultBenchmarkModel.CreateDispatchRequest(request, "configured-provider", "configured-model");
        Assert.False(dispatched.UseBackgroundMode); Assert.False(dispatched.DisableTransportRetries);
        Assert.Equal("configured-provider", dispatched.Provider); Assert.Equal("configured-model", dispatched.Model);
        Assert.Equal(request.ClientRequestId, dispatched.ClientRequestId); Assert.Equal(32768, dispatched.MaxTokens);
        Assert.True(dispatched.StructuredOutputStrict);
        Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, dispatched.StructuredOutputSchema));
        var boundedRuntime = KeyVaultBenchmarkModel.CreateDispatchRequest(request, "configured-provider", "configured-model", execution: true);
        Assert.True(boundedRuntime.DisableTransportRetries);
        dispatched.StructuredOutputSchema!["type"] = "string";
        Assert.Equal(original, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }

    [Fact]
    public void LiveAdapterRejectsToolsBeforeDispatch()
    {
        var request = new LLMRequest { Tools = [new()] };
        Assert.Throws<InvalidOperationException>(() => KeyVaultBenchmarkModel.CreateDispatchRequest(request, "provider", "model"));
    }

    [Theory]
    [InlineData("Allow once")]
    [InlineData("Refuse")]
    public async Task LiveHumanInputRejectsWrongWrappersWithoutAbortingOrChoosingAnAnswer(string choice)
    {
        var payload = new JsonObject { ["mode"] = "choice", ["choices"] = new JsonArray("Allow once", "Refuse"),
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "answer", ["type"] = "select" }) };
        using var input = new StringReader("invalid JSON\n{\"answer\":\"Allow once\"}\n" + new JsonObject { ["response"] = choice }.ToJsonString() + "\n");
        using var output = new StringWriter();
        var response = await LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload, input, output, Ct);
        Assert.Equal(choice, response!["response"]!.GetValue<string>());
        Assert.Contains("Required response schema", output.ToString());
        Assert.Contains("no approval submitted", output.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload,
            new StringReader("{\"answer\":\"Allow once\"}\n"), output, Ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload,
            new StringReader("{\"response\":\"Allow once\"}\n"), output, cancelled.Token));
    }

    [Fact]
    public async Task LiveConsoleCancellationFinishesWithoutInputAndNeverAcceptsALateApproval()
    {
        using var cancelled = new CancellationTokenSource();
        using var reader = new BlockingConsoleReader();
        using var input = TextReader.Synchronized(reader); // Console.In uses this synchronous reader.
        using var output = new StringWriter();
        var payload = new JsonObject { ["mode"] = "confirm", ["choices"] = new JsonArray("approve", "reject") };
        var answer = Task.Run(() => LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload, input, output, cancelled.Token), Ct);
        try
        {
            await reader.Started.Task.WaitAsync(Ct);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        }
        finally { reader.Release.Set(); }
        await reader.Finished.Task.WaitAsync(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
    }

    private sealed class BlockingConsoleReader : TextReader
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public override string ReadLine()
        {
            Started.TrySetResult();
            Release.Wait();
            Finished.TrySetResult();
            return "{\"response\":true}";
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Release.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task HttpRecoveryPreservesUnknownAllowanceAndReplaysAfterReceiptWriteFailure()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recovery");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "immutable", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var ids = new List<string>();
        using var http = new HttpClient(new Handler(message =>
        {
            ids.Add(message.Headers.GetValues("X-Client-Request-Id").Single());
            if (ids.Count == 1) throw new TaskCanceledException();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        Task<LLMResponse> Dispatch(CancellationToken ct) => DispatchAsync(campaign, request.ClientRequestId!, http, ct);
        records.FailCollection = "planning-evaluation-receipts";
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct, allowHttpRecovery: true));
        Assert.Equal(2, ids.Distinct().Count());
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId, ct: Ct);
        Assert.Equal(2, before["calls"]!.GetValue<long>()); Assert.Equal(.1m, before["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1m, before["reserved_cost_eur"]!.GetValue<decimal>()); Assert.Equal(1.1m, before["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(100L, before["reserved_input_tokens"]!.GetValue<long>()); Assert.Equal(20L, before["reserved_output_tokens"]!.GetValue<long>());
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-failures", request.ClientRequestId, Ct));
        records.FailCollection = null;
        campaign = new(records, "recovery");
        var response = await campaign.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct, allowHttpRecovery: true);
        Assert.Equal(1, response.Usage!["uncertain_attempts"]!.GetValue<int>());
        var replay = await campaign.CallAsync(request, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), Ct, allowHttpRecovery: true);
        Assert.True(JsonNode.DeepEquals(response.Usage, replay.Usage)); Assert.Equal(2, ids.Count);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId, ct: Ct)));
        Assert.False(await campaign.HasUncertainRequestAsync(Ct)); // The original uncertain attempt still exists in its HTTP journal.
        Assert.Equal("immutable", (await campaign.LoadAsync("planning-evaluation-requests", request.ClientRequestId, Ct))!["prompt"]!.ToString());
        // Recovery does not poison the rest of the campaign.
        await campaign.CallAsync(new() { ClientRequestId = "next:1:hash" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Null(campaign.StopReason);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(502)]
    public async Task ConservativeAdmissionRejectsCostAndCallExhaustionWithoutChangingLedger(int? status)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "limits");
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 26m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed", Attempts = [new() { Id = "first", Failure = "timeout" }] };
        await journal.SaveAsync(state, Ct);
        if (status is not null) { state.Attempts[0].Status = status; await journal.SaveAsync(state, Ct); }
        state.Attempts.Add(new() { Id = "second" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Single((await journal.LoadAsync(Ct))!.Attempts);
        Assert.Equal(26m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["cost_upper_bound_eur"]!.GetValue<decimal>());
        var other = new BenchmarkCampaign(records, "calls"); var limited = new BenchmarkHttpJournal(other, "session:1:hash", 100, 20, .1m);
        state = new() { Fingerprint = "fixed" };
        for (var i = 0; i < 8; i++) { state.Attempts.Add(new() { Id = i.ToString() }); await limited.SaveAsync(state, Ct); state.Attempts[^1].Status = 503; await limited.SaveAsync(state, Ct); }
        state.Attempts.Add(new() { Id = "ninth" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => limited.SaveAsync(state, Ct));
        Assert.Equal(8, (await limited.LoadAsync(Ct))!.Attempts.Count);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(200, true)]
    [InlineData(400, false)]
    [InlineData(429, false)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    public async Task UnsettledHttpAttemptsUseTheSameConservativeReservationInEveryReport(int? status, bool reserved)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "http-reserves");
        const string id = "run:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 3m);
        var transport = new LLMHttpRetryState { Fingerprint = "fixed", Attempts = [new() { Id = "attempt" }] };
        await journal.SaveAsync(transport, Ct);
        if (status is not null) { transport.Attempts[0].Status = status; await journal.SaveAsync(transport, Ct); }
        var original = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var hash = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var writes = records.Writes;
        var restarted = new BenchmarkCampaign(records, campaign.Id);
        using var model = new KeyVaultBenchmarkModel(restarted, new LLMOptions());
        var campaignUsage = await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct);
        var liveUsage = (await LiveCampaignEvidence.AccountingAsync(restarted, "run"))["planning"]!;
        var partialUsage = (await model.PartialUsageAsync(id, Ct))!;
        var closedUsage = PlanningBenchmarkMeasurements.ClosedHttpUsage([original], 1)!;
        foreach (var usage in new JsonNode[] { campaignUsage, liveUsage, partialUsage, closedUsage })
        {
            Assert.Equal(reserved ? 3m : 0m, usage["reserved_cost_eur"]!.GetValue<decimal>());
            Assert.Equal(reserved ? 100L : 0L, usage["reserved_input_tokens"]!.GetValue<long>());
            Assert.Equal(reserved ? 20L : 0L, usage["reserved_output_tokens"]!.GetValue<long>());
        }
        Assert.Equal(reserved ? 1L : 0L, campaignUsage["unknown_attempts"]!.GetValue<long>());
        Assert.Equal(reserved ? 1 : 0, partialUsage["uncertain_attempts"]!.GetValue<int>());
        Assert.Equal(!reserved, closedUsage["usage_complete"]!.GetValue<bool>());
        Assert.Equal(0L, closedUsage["known_input_tokens"]!.GetValue<long>());
        Assert.Equal(0m, campaignUsage["known_cost_eur"]!.GetValue<decimal>());
        if (status is >= 500 and < 600)
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(new() { ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["benchmark_cost_eur"] = 0m }, Ct));
        Assert.Equal(writes, records.Writes);
        Assert.Equal(hash, PlanningGraphCompiler.Fingerprint((await restarted.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString()));
        Assert.Null(await restarted.LoadAsync("planning-evaluation-receipts", id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterVerifiedUsageKeepsEarlierServerFailureAndTimeoutReservedWithoutRewritingHistoricalUsage(bool historicalUsage)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "later-receipt");
        const string id = "run:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 3m);
        var transport = new LLMHttpRetryState { Fingerprint = "fixed" };
        foreach (var status in new int?[] { null, 502, 200 })
        {
            transport.Attempts.Add(new() { Id = "attempt-" + transport.Attempts.Count });
            await journal.SaveAsync(transport, Ct);
            if (status is not null) { transport.Attempts[^1].Status = status; await journal.SaveAsync(transport, Ct); }
        }
        var known = new JsonObject { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m };
        if (historicalUsage)
        {
            var old = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            old["usage"] = known.DeepClone();
            old["usage"]!["transport_attempts"] = 3; old["usage"]!["uncertain_attempts"] = 1;
            old["usage"]!["reserved_input_tokens"] = 100L; old["usage"]!["reserved_output_tokens"] = 20L;
            old["usage"]!["reserved_cost_eur"] = 3m; old["usage"]!["benchmark_usage_bounded"] = true;
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, old, Ct);
        }
        else await journal.CompleteAsync(known, Ct);
        var original = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var hash = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var writes = records.Writes;
        var restarted = new BenchmarkCampaign(records, campaign.Id);
        using var model = new KeyVaultBenchmarkModel(restarted, new LLMOptions());
        var completed = await new BenchmarkHttpJournal(restarted, id, 100, 20, 3m).CompleteAsync(known, Ct);
        var partial = (await model.PartialUsageAsync(id, Ct))!;
        var accounting = await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct);
        var live = (await LiveCampaignEvidence.AccountingAsync(restarted, "run"))["planning"]!;
        var closed = PlanningBenchmarkMeasurements.ClosedHttpUsage([original], 3)!;
        foreach (var usage in new JsonNode[] { completed, partial, accounting, live, closed })
        {
            Assert.Equal(6m, usage["reserved_cost_eur"]!.GetValue<decimal>());
            Assert.Equal(200L, usage["reserved_input_tokens"]!.GetValue<long>());
            Assert.Equal(40L, usage["reserved_output_tokens"]!.GetValue<long>());
        }
        Assert.Equal(2, completed["uncertain_attempts"]!.GetValue<int>());
        Assert.Equal(10L, partial["input_tokens"]!.GetValue<long>());
        Assert.Equal(.1m, accounting["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(6.1m, accounting["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(10L, closed["known_input_tokens"]!.GetValue<long>());
        Assert.False(closed["usage_complete"]!.GetValue<bool>());
        Assert.Null(closed["input_tokens"]);
        Assert.Equal(writes, records.Writes);
        Assert.Equal(hash, PlanningGraphCompiler.Fingerprint((await restarted.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString()));
    }
    [Fact]
    public async Task HttpExhaustionAndCancellationCannotDispatchOnRestart()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "exhaustion"); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException("unknown delivery"); }));
        var request = new LLMRequest { ClientRequestId = "session:1:hash" };
        for (var restart = 0; restart < 2; restart++)
        {
            campaign = new(records, "exhaustion");
            await Assert.ThrowsAsync<HttpRequestException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
                ct => DispatchAsync(campaign, request.ClientRequestId!, http, ct), Ct, allowHttpRecovery: true));
        }
        Assert.Equal(2, calls); Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId, Ct));
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(2L, accounting["calls"]!.GetValue<long>()); Assert.Equal(2m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal("uncertain_dispatch", campaign.StopReason);
    }
    [Fact]
    public async Task PreparedJournalResumesBeforeFirstSendAndOriginalSchemaCannotChange()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "prepared");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        await new BenchmarkHttpJournal(campaign, request.ClientRequestId, 100, 20, 1m).PrepareAsync(Ct);
        await campaign.SaveAsync("planning-evaluation-requests", request.ClientRequestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}") }; }));
        request.StructuredOutputSchema["type"] = "string";
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, ct => DispatchAsync(campaign, request.ClientRequestId, http, ct), Ct, allowHttpRecovery: true));
        Assert.Equal(0, calls);
        request.StructuredOutputSchema["type"] = "object";
        await campaign.CallAsync(request, _ => Task.CompletedTask, ct => DispatchAsync(campaign, request.ClientRequestId, http, ct), Ct, allowHttpRecovery: true);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task RecoveryPreservesChangedFailureEvidenceWithoutDuplicatingIdenticalFailures()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "history");
        var request = new LLMRequest { ClientRequestId = "session:1:hash" };
        await new BenchmarkHttpJournal(campaign, request.ClientRequestId, 100, 20, 1m).PrepareAsync(Ct);
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, _ => throw new IOException(), Ct, allowHttpRecovery: true));
        for (var restart = 0; restart < 2; restart++)
            await Assert.ThrowsAsync<TimeoutException>(() => new BenchmarkCampaign(records, "history").CallAsync(request, _ => Task.CompletedTask, _ => throw new TimeoutException(), Ct, allowHttpRecovery: true));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", request.ClientRequestId, Ct))!;
        Assert.Equal("TimeoutException", failure["exception_type"]!.ToString());
        Assert.Equal("IOException", Assert.Single(failure["previous_failures"]!.AsArray())!["exception_type"]!.ToString());
    }
    private static async Task<LLMResponse> DispatchAsync(BenchmarkCampaign campaign, string id, HttpClient http, CancellationToken ct)
    {
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        using var context = new LLMHttpRetryContext(id, journal).Activate();
        var response = await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1", RetryPolicy = new() { BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 } }, new() { Prompt = "fixed", MaxOutputTokens = 20 }, ct);
        var usage = await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, ct);
        return new() { Json = response.Json, Text = response.Text, Usage = usage };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request)); }

    [Fact]
    public async Task OfflineReplayKeepsOriginalSchemaAndNeverWritesCampaignRecords()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "original prompt", StructuredOutputSchema = new JsonObject { ["type"] = "string" } };
        var state = new PlanningSession { Request = new() { TenantId = "benchmark", SessionId = "session", Name = "local", Prompt = "original prompt",
            MaxModelCalls = 8, MaxReplanAttempts = 2, Generation = new("medium", 12000, 8192) }, ModelCalls = 3, ReplanAttempts = 2 };
        await campaign.SaveAsync("planning-evaluation-runs", "source:pilot:local:1", new() { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", request.ClientRequestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        await campaign.SaveAsync("planning-evaluation-receipts", request.ClientRequestId, JsonSerializer.SerializeToNode(new LLMResponse { Text = "\"recorded\"" }, PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        var before = records.Writes;
        var snapshot = await campaign.InspectAsync(Ct);
        var replay = await campaign.ReadReplayAsync("source:pilot:local:1", Ct);
        Assert.False(replay.Client is ILLMCapabilityResolver);
        var reserved = replay.State.PendingCall!.Request;
        Assert.Equal("string", reserved.StructuredOutputSchema!["type"]!.ToString());
        reserved.StructuredOutputSchema["type"] = "object";
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.Client.CallAsync(reserved, Ct));
        reserved.StructuredOutputSchema["type"] = "string";
        Assert.Equal("\"recorded\"", (await replay.Client.CallAsync(reserved, Ct)).Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.Client.CallAsync(reserved, Ct));
        Assert.Equal(before, records.Writes);
        Assert.True(JsonNode.DeepEquals(snapshot, await campaign.InspectAsync(Ct)));
        var unchanged = (await campaign.LoadAsync("planning-evaluation-runs", "source:pilot:local:1", Ct))!["session"]!;
        Assert.Equal(3, unchanged["modelCalls"]!.GetValue<int>()); Assert.Equal(2, unchanged["replanAttempts"]!.GetValue<int>());
        Assert.Equal(8, unchanged["request"]!["maxModelCalls"]!.GetValue<int>());
    }

    private static async Task<(Records Records, BenchmarkCampaign Campaign)> PendingRecoveryAsync(bool receipt = true, string tenant = "benchmark")
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recovery");
        var request = new LLMRequest { ClientRequestId = "pending:4:hash", Prompt = "unchanged",
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"plan":{"type":["object","null"]}}}""") };
        var session = new PlanningSession { IntentVersion = 2, Revision = 5, Status = PlanningStatus.Generating,
            Request = new() { TenantId = tenant, SessionId = "pending", Name = "retained", Prompt = "original" },
            ModelCalls = 4, ActiveMilliseconds = 1000, Catalog = new(),
            PendingCall = new() { Id = request.ClientRequestId, Purpose = "tasks", Request = request } };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:pending", new()
        {
            ["source"] = "original-build", ["manifest"] = new JsonObject { ["frozen"] = true },
            ["session"] = JsonSerializer.SerializeToNode(session, PlanningJsonContext.Default.PlanningSession),
            ["result"] = new JsonObject { ["status"] = "generating", ["planning_ms"] = 1500L, ["calls"] = 4,
                ["execution_status"] = "not_started", ["accounting"] = new JsonObject { ["retained"] = 42 } }
        }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", request.ClientRequestId,
            JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        if (receipt) await campaign.SaveAsync("planning-evaluation-receipts", request.ClientRequestId,
            JsonSerializer.SerializeToNode(new LLMResponse { Json = JsonNode.Parse("""{"plan":null,"discoveryRequests":null,"clarifications":null}""") },
                PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        return (records, campaign);
    }

    [Fact]
    public async Task ValidPendingReceiptReachesReviewWithoutApprovingOrExecuting()
    {
        var (_, campaign) = await PendingRecoveryAsync();
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        var state = run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        state.Requirements = new() { Summary = "Return a greeting", Inputs = [],
            Outcomes = [new("greeting", "Return a greeting")], Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }] };
        var runtime = new GnOuGo.Flow.Planning.WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        run["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:pending", run, Ct);
        await campaign.SaveAsync("planning-evaluation-receipts", "pending:4:hash",
            JsonSerializer.SerializeToNode(new LLMResponse { Json = JsonSerializer.SerializeToNode(new PlanningProposal
                { Plan = GnOuGo.Planning.Examples.PlanningCorpus.Greeting() }, PlanningJsonContext.Default.PlanningProposal) },
                PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        var result = await campaign.RecoverPendingReceiptAsync("pending", 5, "pending:4:hash", 600, "validator-build", true, Ct);
        Assert.Equal(PlanningStatus.FinalReview, result["status"]!.ToString());
        Assert.Empty(result["diagnostics"]!.AsArray());
        var saved = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        Assert.NotNull(saved["artifact_hash"]); Assert.Null(saved["session"]!["approvedHash"]);
        Assert.Null(saved["execution_started"]); Assert.Equal(4, saved["result"]!["calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task PendingReceiptRecoveryCommitsOnceWithoutChangingFrozenEvidenceOrAccounting()
    {
        var (records, campaign) = await PendingRecoveryAsync();
        var original = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        var before = await campaign.InspectAsync(Ct); var writes = records.Writes;
        var dry = await campaign.RecoverPendingReceiptAsync("pending", 5, "pending:4:hash", 600, "validator-build", false, Ct);
        Assert.False(dry["applied"]!.GetValue<bool>()); Assert.Equal(writes, records.Writes);
        var applied = await campaign.RecoverPendingReceiptAsync("pending", 5, "pending:4:hash", 600, "validator-build", true, Ct);
        Assert.Equal(6L, applied["revision"]!.GetValue<long>()); Assert.Equal(0, applied["new_model_calls"]!.GetValue<int>());
        Assert.NotEmpty(applied["diagnostics"]!.AsArray()); // Stop here; do not ask for another proposal.
        Assert.Equal(writes + 1, records.Writes);
        var restarted = new BenchmarkCampaign(records, "recovery");
        var recovered = (await restarted.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        Assert.Null(recovered["session"]!["pendingCall"]); Assert.Equal(4, recovered["session"]!["modelCalls"]!.GetValue<int>());
        Assert.True(recovered["session"]!["activeMilliseconds"]!.GetValue<double>() >= 1600);
        Assert.True(recovered["result"]!["planning_ms"]!.GetValue<long>() >= 2100);
        foreach (var key in new[] { "source", "manifest" }) Assert.True(JsonNode.DeepEquals(original[key], recovered[key]));
        Assert.True(JsonNode.DeepEquals(await LiveCampaignEvidence.AccountingAsync(restarted, "pending"), recovered["result"]!["accounting"]));
        Assert.True(JsonNode.DeepEquals(original["result"], recovered["receipt_recoveries"]![0]!["result_before"]));
        Assert.True(JsonNode.DeepEquals(original["session"], Assert.Single(recovered["receipt_recoveries"]!.AsArray())!["session_before"]));
        Assert.True(JsonNode.DeepEquals(before, await restarted.InspectAsync(Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecoverPendingReceiptAsync("pending", 5, "pending:4:hash", 600, "validator-build", true, Ct));
        Assert.Equal(writes + 1, records.Writes);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("tenant")]
    [InlineData("receipt")]
    [InlineData("schema")]
    [InlineData("identity")]
    [InlineData("executed")]
    [InlineData("closed")]
    [InlineData("elapsed")]
    public async Task PendingReceiptRecoveryRejectsChangedOrUncertainAuthorityWithoutWrites(string failure)
    {
        var (records, campaign) = await PendingRecoveryAsync(receipt: failure != "receipt", tenant: failure == "tenant" ? "other" : "benchmark");
        if (failure == "schema")
        {
            var request = (await campaign.LoadAsync("planning-evaluation-requests", "pending:4:hash", Ct))!;
            request["structuredOutputSchema"]!["type"] = "string";
            await campaign.SaveAsync("planning-evaluation-requests", "pending:4:hash", request, Ct);
        }
        if (failure == "executed")
        {
            var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
            run["execution_started"] = "retained";
            await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:pending", run, Ct);
        }
        if (failure == "closed") await campaign.SaveAsync("planning-evaluation-closures", "pending:4:hash", new(), Ct);
        var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RecoverPendingReceiptAsync("pending", failure == "stale" ? 4 : 5,
            failure == "identity" ? "pending:3:hash" : "pending:4:hash", failure == "elapsed" ? 1_800_000 : 600, "validator-build", true, Ct));
        Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public async Task InterruptedRecoveryPublicationLeavesOriginalReceiptAvailableWithoutDuplicateCharging()
    {
        var (records, campaign) = await PendingRecoveryAsync();
        var original = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct);
        var before = await campaign.InspectAsync(Ct);
        records.FailCollection = SchemaPortabilityCampaign.Collection;
        await Assert.ThrowsAsync<IOException>(() => campaign.RecoverPendingReceiptAsync("pending", 5, "pending:4:hash", 600, "validator-build", true, Ct));
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct)));
        records.FailCollection = null;
        await new BenchmarkCampaign(records, "recovery").RecoverPendingReceiptAsync("pending", 5, "pending:4:hash", 600, "validator-build", true, Ct);
        Assert.True(JsonNode.DeepEquals(before, await campaign.InspectAsync(Ct)));
    }
    [Fact]
    public async Task OfflineReplayWithoutReceiptFailsWithoutReservingOrWriting()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        var state = new PlanningSession { Request = new() { SessionId = "session", Name = "local" } };
        await campaign.SaveAsync("planning-evaluation-runs", "source:pilot:local:1", new() { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", "session:1:hash", JsonSerializer.SerializeToNode(new LLMRequest { ClientRequestId = "session:1:hash" }, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var before = records.Writes;
        Assert.Contains("No completion receipt", (await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ReadReplayAsync("source:pilot:local:1", Ct))).Message);
        Assert.Equal(before, records.Writes); Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkCampaign(records, "different").ReadReplayAsync("source:pilot:local:1", Ct));
    }
    [Fact]
    public async Task UncertainFailureRetainsSafeMetadataWithoutInventingAReceipt()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        await Assert.ThrowsAsync<LLMClientException>(() => campaign.CallAsync(new() { ClientRequestId = "session:1:hash", Prompt = "private prompt" }, _ => Task.CompletedTask,
            _ => throw new LLMClientException(LLMClientFailureKind.ServiceUnavailable, "raw body containing private data", true, 503, "overloaded", 1, false, 1000), Ct));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", "session:1:hash", Ct))!;
        Assert.Equal("ServiceUnavailable", failure["kind"]!.ToString()); Assert.Equal(503, failure["status_code"]!.GetValue<int>());
        Assert.Equal("dispatch", failure["stage"]!.ToString()); Assert.Equal(1, failure["attempt_count"]!.GetValue<int>());
        Assert.DoesNotContain("private", failure.ToJsonString()); Assert.DoesNotContain("raw body", failure.ToJsonString());
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", "session:1:hash", Ct));
        var writes = records.Writes; var inspection = await campaign.InspectAsync(Ct);
        Assert.Equal(1, inspection["reserved_requests"]!.GetValue<int>()); Assert.Equal(0, inspection["completed_receipts"]!.GetValue<int>());
        Assert.False(inspection["completion_receipts_complete"]!.GetValue<bool>()); Assert.Null(inspection["known_budget_cost"]);
        Assert.Equal("session:1:hash", Assert.Single(inspection["uncertain_requests"]!.AsArray())!.ToString());
        Assert.Equal(writes, records.Writes);
    }
    [Fact]
    public async Task NewCampaignIgnoresOldUncertaintyAndReplaysOwnReceiptsWithoutDispatch()
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var fresh = new BenchmarkCampaign(records, "fresh");
        await old.SaveAsync("planning-evaluation-requests", "pending", new() { ["private"] = "unchanged" }, Ct);
        var request = new LLMRequest { ClientRequestId = "session:1", StructuredOutputSchema = new JsonObject { ["type"] = "object" } }; var calls = 0;
        Task<LLMResponse> Dispatch(CancellationToken _) { calls++; return Task.FromResult(new LLMResponse { Text = "receipt" }); }
        Assert.Equal("receipt", (await fresh.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct)).Text);
        var reopened = new BenchmarkCampaign(records, "fresh");
        Assert.Equal("receipt", (await reopened.CallAsync(request, _ => throw new InvalidOperationException("Preflight must not rerun"), Dispatch, Ct)).Text);
        Assert.Equal(1, calls); Assert.True(await old.HasUncertainRequestAsync(Ct)); Assert.False(await fresh.HasUncertainRequestAsync(Ct));
        Assert.Equal("unchanged", (await old.LoadAsync("planning-evaluation-requests", "pending", Ct))!["private"]!.ToString());
    }
    [Fact]
    public async Task UncertainDispatchBlocksFurtherRequestsWithoutResend()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh"); var calls = 0;
        Task<LLMResponse> Dispatch(CancellationToken _) { calls++; throw new IOException("Uncertain transport"); }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(new() { ClientRequestId = "one" }, _ => Task.CompletedTask, Dispatch, Ct));
        var reopened = new BenchmarkCampaign(records, "fresh");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CallAsync(new() { ClientRequestId = "two" }, _ => Task.CompletedTask, Dispatch, Ct));
        Assert.Equal(1, calls); Assert.Equal("uncertain_dispatch", reopened.StopReason);
    }
    [Fact]
    public async Task CompletedReceiptSurvivesCancellationAndPinnedConfigurationCannotDrift()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh");
        await campaign.PinAsync(new() { ["model"] = "one" }, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.PinAsync(new() { ["model"] = "two" }, Ct));
        using var cancelled = new CancellationTokenSource();
        await campaign.CallAsync(new() { ClientRequestId = "one" }, _ => Task.CompletedTask, _ => { cancelled.Cancel(); return Task.FromResult(new LLMResponse { Text = "durable" }); }, cancelled.Token);
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-receipts", "one", Ct));
    }
    [Fact]
    public async Task CheckpointsRetainReservedSchemaAndBudgetRefusalDoesNotReserveDispatch()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh");
        var evidence = new JsonObject { ["session"] = new JsonObject { ["modelCalls"] = 1, ["pendingCall"] = new JsonObject { ["responseSchema"] = new JsonObject { ["type"] = "string" } } } };
        await campaign.SaveAsync("planning-evaluation-runs", "pilot:local:1", evidence, Ct);
        Assert.True(JsonNode.DeepEquals(evidence, await new BenchmarkCampaign(records, "fresh").LoadAsync("planning-evaluation-runs", "pilot:local:1", Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "one" }, _ => throw new InvalidOperationException("budget"), _ => throw new InvalidOperationException("must not dispatch"), Ct));
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExhaustedRunClosureRetainsUncertaintyAndCostWithoutAllowingRedispatch(bool liveWorkflow)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "retained");
        var request = new LLMRequest { ClientRequestId = "session:1:hash" };
        var journal = new BenchmarkHttpJournal(campaign, request.ClientRequestId, 100, 20, 1m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed" };
        for (var i = 0; i < 8; i++) { state.Attempts.Add(new() { Id = i.ToString() }); await journal.SaveAsync(state, Ct); }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, _ => throw new IOException(), Ct));
        var run = new JsonObject { ["session"] = new JsonObject { ["pendingCall"] = new JsonObject { ["id"] = request.ClientRequestId } },
            ["result"] = new JsonObject { ["execution_correct"] = false, ["termination_reason"] = "session_http_budget" } };
        var collection = liveWorkflow ? SchemaPortabilityCampaign.Collection : "planning-evaluation-runs";
        if (liveWorkflow) run["result"] = new JsonObject { ["status"] = "stopped", ["execution_status"] = "not_started", ["execution_oracle"] = false };
        await campaign.SaveAsync(collection, "source:pilot:case:1", run, Ct);
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        await campaign.RetainInconclusiveAsync("source:pilot:case:1", Ct, liveWorkflow);
        var writes = records.Writes;
        await campaign.RetainInconclusiveAsync("source:pilot:case:1", Ct, liveWorkflow);
        Assert.Equal(writes, records.Writes);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.True(JsonNode.DeepEquals(run, await campaign.LoadAsync(collection, "source:pilot:case:1", Ct)));
        Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, _ => throw new Exception("Must not dispatch"), Ct, allowHttpRecovery: true));
        await campaign.CallAsync(new() { ClientRequestId = "different-session:1:hash" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Equal(8m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Single((await campaign.InspectAsync(Ct))["retained_inconclusive_requests"]!.AsArray());
    }
    [Fact]
    public async Task InconclusiveClosureRequiresSavedFailedRunAndExhaustedAllowance()
    {
        var campaign = new BenchmarkCampaign(new Records(), "retained");
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainInconclusiveAsync("missing", Ct));
        await campaign.SaveAsync("planning-evaluation-runs", "run", new() { ["session"] = new JsonObject { ["pendingCall"] = new JsonObject { ["id"] = "session:1:hash" } } }, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainInconclusiveAsync("run", Ct));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-closures", "session:1:hash", Ct));
    }
    [Theory]
    [InlineData("remaining_attempts")][InlineData("execution_started")][InlineData("completed")]
    [InlineData("running")][InlineData("receipt")][InlineData("wrong_identity")]
    public async Task LivePlanningClosureCannotArchiveActiveOrExecutedWork(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "retained-live");
        const string id = "session:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed" };
        for (var i = 0; i < (defect == "remaining_attempts" ? 7 : 8); i++)
        { state.Attempts.Add(new() { Id = i.ToString() }); await journal.SaveAsync(state, Ct); }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(new() { ClientRequestId = id }, _ => Task.CompletedTask, _ => throw new IOException(), Ct));
        var run = new JsonObject { ["session"] = new JsonObject { ["pendingCall"] = new JsonObject { ["id"] = id } },
            ["result"] = new JsonObject { ["status"] = "stopped", ["execution_status"] = "not_started", ["execution_oracle"] = false } };
        if (defect == "execution_started") run["execution_started"] = "retained";
        if (defect == "completed") run["result"]!["execution_oracle"] = true;
        if (defect == "running") run["result"]!["status"] = "generating";
        if (defect == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct);
        if (defect == "wrong_identity") await campaign.SaveAsync("planning-evaluation-requests", id, new() { ["clientRequestId"] = "different" }, Ct);
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:session", run, Ct);
        var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainInconclusiveAsync("run:session", Ct, liveWorkflow: true));
        Assert.Equal(writes, records.Writes);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("remaining_allowance")]
    [InlineData("missing_limit")]
    [InlineData("invalid_limit")]
    [InlineData("wrong_count")]
    [InlineData("missing_exhaustion")]
    [InlineData("not_retryable")]
    [InlineData("cancelled")]
    [InlineData("permanent")]
    [InlineData("terminal_response")]
    [InlineData("wrong_kind")]
    public async Task ExhaustedUncertaintyAllowanceRetainsTheOriginalRequestAndFullReservations(string? defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "uncertainty-limit");
        const string id = "old-session:1:hash";
        var request = new LLMRequest { ClientRequestId = id };
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed" };
        for (var i = 0; i < 2; i++)
        {
            state.Attempts.Add(new() { Id = "attempt-" + i }); await journal.SaveAsync(state, Ct);
            state.Attempts[^1].Failure = i == 0 ? "timeout" : defect is "cancelled" or "permanent" ? defect : "transport";
            if (defect == "terminal_response" && i == 1) state.Attempts[^1].Status = 503;
            await journal.SaveAsync(state, Ct);
        }
        await Assert.ThrowsAsync<LLMClientException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
            _ => throw new LLMClientException(defect == "wrong_kind" ? LLMClientFailureKind.Unknown : LLMClientFailureKind.Transport,
                "Retained uncertainty.", retryable: defect != "not_retryable", statusCode: null, safeProviderCode: null,
                attemptCount: defect == "wrong_count" ? 1 : 2, retryExhausted: defect != "missing_exhaustion", retryAfterMilliseconds: null), Ct));
        var policy = new JsonObject { ["MaxAttempts"] = 4, ["MaxUncertainRetries"] = defect == "remaining_allowance" ? 2 : defect == "invalid_limit" ? -1 : 1 };
        if (defect == "missing_limit") policy.Remove("MaxUncertainRetries");
        await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", policy, Ct);
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var original = await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct);
        var originalRequest = await campaign.LoadAsync("planning-evaluation-requests", id, Ct);
        var originalFailure = await campaign.LoadAsync("planning-evaluation-failures", id, Ct);
        if (defect is not null)
        {
            var writes = records.Writes;
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(id, Ct));
            Assert.Equal(writes, records.Writes); return;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "fresh:1" },
            _ => Task.CompletedTask, _ => throw new Exception("Must not dispatch"), Ct));
        await campaign.RetainExhaustedRequestAsync(id, Ct);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct)));
        Assert.True(JsonNode.DeepEquals(originalRequest, await campaign.LoadAsync("planning-evaluation-requests", id, Ct)));
        Assert.True(JsonNode.DeepEquals(originalFailure, await campaign.LoadAsync("planning-evaluation-failures", id, Ct)));
        Assert.True(JsonNode.DeepEquals(policy, await campaign.LoadAsync("planning-evaluation-configuration", "http-retry-policy", Ct)));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", id, Ct));
        var reopened = new BenchmarkCampaign(records, "uncertainty-limit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CallAsync(request, _ => Task.CompletedTask,
            _ => throw new Exception("Must not replay"), Ct, allowHttpRecovery: true));
        await reopened.CallAsync(new() { ClientRequestId = "fresh:1" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Equal(2m, (await BenchmarkHttpJournal.AccountingAsync(reopened, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        var denied = new BenchmarkHttpJournal(reopened, "over-budget:1", 100, 20, 49m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.SaveAsync(new() { Attempts = [new() { Id = "new" }] }, Ct));
    }
    [Theory]
    [InlineData(null, 3)]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData(null, 2)]
    [InlineData("remaining_attempts", 0)]
    [InlineData("missing_policy", 0)]
    [InlineData("receipt", 0)]
    [InlineData("verified_usage", 0)]
    [InlineData("completed_http", 0)]
    [InlineData("earlier_success", 3)]
    [InlineData("earlier_success", 2)]
    [InlineData("no_uncertainty", -1)]
    [InlineData("missing_exhaustion", 0)]
    [InlineData("wrong_attempt_count", 0)]
    [InlineData("wrong_terminal_status", 0)]
    [InlineData("not_retryable", 0)]
    [InlineData("wrong_identity", 0)]
    [InlineData("missing_failure", 0)]
    public async Task ExplicitTransportExhaustionRetainsAllEvidenceAndReservations(string? defect, int unknownIndex)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "transport-exhaustion");
        const string id = "old-session:1:hash";
        var request = new LLMRequest { ClientRequestId = id };
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed" };
        for (var i = 0; i < 4; i++)
        {
            state.Attempts.Add(new() { Id = "attempt-" + i }); await journal.SaveAsync(state, Ct);
            if (i != unknownIndex) { state.Attempts[^1].Status = 500; await journal.SaveAsync(state, Ct); }
        }
        if (unknownIndex == 3)
            await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, _ => throw new IOException(), Ct));
        else
            await Assert.ThrowsAsync<LLMClientException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
                _ => throw new LLMClientException(LLMClientFailureKind.ServiceUnavailable, "Retained HTTP failure.",
                    retryable: defect != "not_retryable", statusCode: defect == "wrong_terminal_status" ? 503 : 500,
                    safeProviderCode: null, attemptCount: defect == "wrong_attempt_count" ? 3 : 4,
                    retryExhausted: defect != "missing_exhaustion", retryAfterMilliseconds: null), Ct));
        if (defect != "missing_policy") await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", new() { ["MaxAttempts"] = defect == "remaining_attempts" ? 5 : 4 }, Ct);
        if (defect == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct);
        if (defect is "verified_usage" or "completed_http" or "earlier_success")
        {
            var row = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            if (defect == "verified_usage") row["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 2, ["benchmark_cost_eur"] = .01m };
            else row["transport"]!["Attempts"]![defect == "earlier_success" ? 1 : 3]!["Status"] = 200;
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, row, Ct);
        }
        if (defect == "wrong_identity") await campaign.SaveAsync("planning-evaluation-requests", id, new() { ["clientRequestId"] = "other" }, Ct);
        if (defect == "missing_failure") await campaign.SaveAsync("planning-evaluation-failures", id, new(), Ct);
        if (defect is not null)
        {
            var writes = records.Writes;
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(id, Ct));
            Assert.Equal(writes, records.Writes); return;
        }
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var original = await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct);
        var originalRequest = await campaign.LoadAsync("planning-evaluation-requests", id, Ct);
        var originalFailure = await campaign.LoadAsync("planning-evaluation-failures", id, Ct);
        var originalPolicy = await campaign.LoadAsync("planning-evaluation-configuration", "http-retry-policy", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "fresh:1" }, _ => Task.CompletedTask, _ => throw new Exception("Must not dispatch"), Ct));
        await campaign.RetainExhaustedRequestAsync(id, Ct);
        var savedWrites = records.Writes; await campaign.RetainExhaustedRequestAsync(id, Ct); Assert.Equal(savedWrites, records.Writes);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct)));
        Assert.True(JsonNode.DeepEquals(originalRequest, await campaign.LoadAsync("planning-evaluation-requests", id, Ct)));
        Assert.True(JsonNode.DeepEquals(originalFailure, await campaign.LoadAsync("planning-evaluation-failures", id, Ct)));
        Assert.True(JsonNode.DeepEquals(originalPolicy, await campaign.LoadAsync("planning-evaluation-configuration", "http-retry-policy", Ct)));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", id, Ct));
        var reopened = new BenchmarkCampaign(records, "transport-exhaustion");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CallAsync(request, _ => Task.CompletedTask, _ => throw new Exception("Must not replay"), Ct, allowHttpRecovery: true));
        await reopened.CallAsync(new() { ClientRequestId = "fresh:1" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Equal(4m, (await BenchmarkHttpJournal.AccountingAsync(reopened, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        var denied = new BenchmarkHttpJournal(reopened, "over-budget:1", 100, 20, 50m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.SaveAsync(new() { Attempts = [new() { Id = "new" }] }, Ct));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("missing_closure")]
    [InlineData("changed_run")]
    [InlineData("changed_request")]
    [InlineData("attempted")]
    [InlineData("receipt")]
    [InlineData("failure")]
    [InlineData("wrapped")]
    public async Task AdmissionAuditRequiresImmutableClosedZeroDispatchEvidenceAndNeverWrites(string? defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "audit");
        var first = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 1m);
        var state = new LLMHttpRetryState { Fingerprint = "first" };
        for (var i = 0; i < 8; i++)
        {
            state.Attempts.Add(new() { Id = i.ToString() }); await first.SaveAsync(state, Ct);
            state.Attempts[^1].Status = i == 7 ? 200 : 503; await first.SaveAsync(state, Ct);
        }
        var known = await first.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, Ct);
        const string id = "session:2:hash", key = "source:fixture:case:1";
        var denied = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m); await denied.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = id }, _ => Task.CompletedTask,
            async ct => { await denied.SaveAsync(new() { Attempts = [new() { Id = "denied" }] }, ct); throw new Exception("Must not dispatch"); }, Ct));
        var run = new JsonObject { ["session"] = new JsonObject { ["modelCalls"] = 2, ["pendingCall"] = new JsonObject { ["id"] = id } },
            ["result"] = new JsonObject { ["execution_correct"] = false, ["termination_reason"] = "session_http_budget", ["calls"] = 9 },
            ["usage_receipts"] = new JsonObject { ["session:1:hash"] = known, [id] = new JsonObject { ["transport_attempts"] = 0 } } };
        await campaign.SaveAsync("planning-evaluation-runs", key, run, Ct);
        if (defect == "wrapped")
            await campaign.SaveAsync("planning-evaluation-failures", id, new()
            { ["stage"] = "dispatch", ["exception_type"] = "LLMClientException", ["kind"] = "Unknown", ["retryable"] = false }, Ct);
        if (defect != "missing_closure") await campaign.RetainInconclusiveAsync(key, Ct);
        switch (defect)
        {
            case "changed_run": run["result"]!["calls"] = 10; break;
            case "changed_request": await campaign.SaveAsync("planning-evaluation-requests", id, new() { ["prompt"] = "changed" }, Ct); break;
            case "attempted":
                var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
                journal["transport"]!["Attempts"]!.AsArray().Add((JsonNode)new JsonObject { ["Id"] = "unknown", ["Status"] = null });
                await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, journal, Ct); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct); break;
            case "failure": await campaign.SaveAsync("planning-evaluation-failures", id, new() { ["exception_type"] = "IOException" }, Ct); break;
        }
        var writes = records.Writes; var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var audit = await campaign.AuditAdmissionDenialAsync(key, run, Ct);
        Assert.Equal(defect is null or "wrapped", audit is not null); Assert.Equal(writes, records.Writes);
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        if (audit is not null)
        {
            Assert.Equal(0, audit["admitted_request_attempts"]!.GetValue<int>());
            Assert.Equal(8, audit["session_attempts"]!.GetValue<int>());
            Assert.Equal(9, audit["original_result"]!["calls"]!.GetValue<int>());
            Assert.False(audit["original_result"]!["execution_correct"]!.GetValue<bool>());
        }
    }
    private sealed class Records : IKeyVaultRecordStore
    {
        internal int Writes;
        internal string? FailCollection;
        private readonly Dictionary<string, KeyVaultRecordValue> _rows = [];
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_rows.GetValueOrDefault(c + t + k)); }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); if (c == FailCollection) throw new IOException("Injected store failure"); Writes++; var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _rows[c + t + k] = row; return Task.FromResult(row); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Values.Where(r => r.Collection == c && r.TenantId == t).ToArray());
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
