using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    private const string CancelledRun = "server-cancelled-run";

    [Theory]
    [InlineData("ChatCompletions", "text/event-stream; charset=utf-8")]
    [InlineData("Responses", "text/event-stream")]
    [InlineData("Messages", "text/event-stream")]
    [InlineData("OllamaChat", "application/x-ndjson")]
    public async Task CancelledNativePartialRetentionPreservesAllBytesAndHoldsAcrossRestart(string protocol, string contentType)
    {
        var records = new Records(); var campaign = await SeedCancelledNativePartialAsync(records, protocol, contentType);
        var original = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId, Ct,
            BenchmarkCampaign.CancelledPlanningRetention);
        await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.CancelledPlanningRetention);
        var restarted = new BenchmarkCampaign(records, "retention");
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        Assert.Equal(1m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null(await restarted.LoadAsync("planning-evaluation-receipts", RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
        await restarted.RequireRunNotRetainedAsync("independent-fresh-session", Ct);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("usage")]
    [InlineData("assembled")]
    [InlineData("body_complete")]
    [InlineData("termination")]
    [InlineData("provider_error")]
    [InlineData("stream_failure")]
    [InlineData("non_streaming")]
    [InlineData("wrong_mode")]
    [InlineData("wrong_protocol")]
    [InlineData("wrong_media")]
    [InlineData("http_failure")]
    [InlineData("unmatched_cancellation")]
    [InlineData("transport_failure")]
    [InlineData("invalid_bytes")]
    [InlineData("mismatched_text")]
    [InlineData("empty_bytes")]
    [InlineData("missing_started")]
    [InlineData("missing_completed")]
    [InlineData("negative_elapsed")]
    public async Task CancelledNativeRetentionRejectsCompletionOrUnprovedTransport(string defect)
    {
        var records = new Records(); var campaign = await SeedCancelledNativePartialAsync(records, "ChatCompletions", "text/event-stream");
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var attempt = journal["transport"]!["Attempts"]![0]!;
        var transport = journal["transport"]!["Transport"]!;
        switch (defect)
        {
            case "complete": attempt["ProtocolComplete"] = true; break;
            case "usage": attempt["UsageKnown"] = true; break;
            case "assembled": attempt["AssembledBody"] = "{}"; break;
            case "body_complete": attempt["ResponseBodyComplete"] = true; break;
            case "termination": attempt["TerminationStatus"] = "stop"; break;
            case "provider_error": attempt["ProviderErrorCode"] = "error"; break;
            case "stream_failure": attempt["StreamFailure"] = "failure"; break;
            case "non_streaming": transport["IsStreaming"] = false; break;
            case "wrong_mode": transport["EffectiveMode"] = "NonStreaming"; break;
            case "wrong_protocol": transport["Protocol"] = "Unknown"; break;
            case "wrong_media": attempt["ContentType"] = "application/json"; break;
            case "http_failure": attempt["ObservedStatus"] = 500; break;
            case "unmatched_cancellation": attempt["RetryDecision"] = "other"; break;
            case "transport_failure": attempt["Failure"] = "transport"; break;
            case "invalid_bytes": attempt["PartialBodyBase64"] = "invalid!"; break;
            case "mismatched_text": attempt["PartialBody"] = "unrelated text"; break;
            case "empty_bytes": attempt["PartialBodyBase64"] = ""; break;
            case "missing_started": attempt["StartedAtUtc"] = null; break;
            case "missing_completed": attempt["CompletedAtUtc"] = null; break;
            case "negative_elapsed": attempt["CompletedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-10); break;
        }
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var original = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun,
            RequestId, Ct, BenchmarkCampaign.CancelledPlanningRetention));
        Assert.Equal(original, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedCancelledNativePartialAsync(Records records, string protocol, string contentType)
    {
        var campaign = await SeedCancelledServerAsync(records);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Transport"] = new JsonObject
        { ["RequestedMode"] = "Streaming", ["EffectiveMode"] = "Streaming", ["IsStreaming"] = true, ["Protocol"] = protocol };
        var attempt = journal["transport"]!["Attempts"]![0]!;
        attempt["Failure"] = "cancelled"; attempt["RetryDecision"] = "cancelled; no retry"; attempt["ResponseBodyComplete"] = false;
        attempt["ObservedStatus"] = 200; attempt["ContentType"] = contentType;
        attempt["PartialBodyBase64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("data: {\"partial\":true}\n\n"));
        attempt["PartialBody"] = "data: {\"partial\":true}\n\n";
        attempt["StartedAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(-10); attempt["CompletedAtUtc"] = DateTimeOffset.UtcNow;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        return campaign;
    }

    [Fact]
    public async Task CancelledIndependentPolicyRetainsItsExactCaptureAndNeverResumesTheCancelledRequest()
    {
        var records = new Records(); var campaign = await SeedCancelledServerAsync(records);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", CancelledRun, Ct))!;
        var capture = run["session"]!["request"]!["options"]!["planning_transport_policy"]!;
        capture["version"] = 2; capture["maxUncertainRetries"] = 1; capture["independentGenerationMaxElapsedMilliseconds"] = 1_260_000;
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        policy["planning_transport_policy"] = capture.DeepClone();
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        await campaign.SaveAsync("planning-evaluation-runs", CancelledRun, run, Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var transport = journal["effective_transport_policy"]!;
        transport["planning_transport_policy"] = capture.DeepClone(); transport["effective_retry_policy"]!["MaxUncertainRetries"] = 1;
        journal["effective_transport_policy_hash"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(transport.ToJsonString());
        var attempt = journal["transport"]!["Attempts"]![0]!;
        attempt["Failure"] = "cancelled"; attempt["RetryDecision"] = "cancelled; no retry"; attempt["ResponseBodyComplete"] = false;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var original = records.Snapshot(excludeClosures: true);
        var preview = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId, Ct, BenchmarkCampaign.CancelledPlanningRetention);
        await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId, preview["evidence_hash"]!.ToString(), Operator, Authorization, Ct,
            BenchmarkCampaign.CancelledPlanningRetention);
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RequireRunNotRetainedAsync(Session, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledServerGenerationRetentionPreservesReservationAndForbidsReplayAcrossRestart(bool explicitTransportCancellation)
    {
        var records = new Records(); var campaign = await SeedCancelledServerAsync(records);
        if (explicitTransportCancellation)
        {
            var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
            var attempt = journal["transport"]!["Attempts"]![0]!;
            attempt["Failure"] = "cancelled"; attempt["RetryDecision"] = "cancelled; no retry"; attempt["ResponseBodyComplete"] = false;
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        }
        var original = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId, Ct,
            BenchmarkCampaign.CancelledPlanningRetention);
        await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.CancelledPlanningRetention);
        var restarted = new BenchmarkCampaign(records, "retention");
        await restarted.ApplyPermanentRetentionAsync("planning-evaluation-runs", CancelledRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, BenchmarkCampaign.CancelledPlanningRetention);
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        Assert.Equal(1m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null(await restarted.LoadAsync("planning-evaluation-receipts", RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
        await restarted.RequireRunNotRetainedAsync("independent-fresh-session", Ct);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("session")]
    [InlineData("configuration")]
    [InlineData("provider")]
    [InlineData("metadata")]
    [InlineData("elapsed")]
    [InlineData("threshold")]
    [InlineData("strict_authority")]
    [InlineData("status")]
    [InlineData("execution")]
    [InlineData("tools")]
    [InlineData("receipt")]
    [InlineData("usage")]
    [InlineData("body")]
    [InlineData("failure")]
    [InlineData("partial_body")]
    [InlineData("transport_failure")]
    [InlineData("unmatched_cancellation")]
    [InlineData("complete_body")]
    public async Task CancelledServerRetentionRejectsSubstitutionEffectsAndReceivedOutcomes(string defect)
    {
        var records = new Records(); var campaign = await SeedCancelledServerAsync(records);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", CancelledRun, Ct))!;
        switch (defect)
        {
            case "host": run["host"] = "other"; break;
            case "session": run["session_id"] = "other"; break;
            case "configuration": run["campaign_configuration"] = new string('d', 64); break;
            case "provider": run["provider"] = null; break;
            case "metadata": run["session"]!["request"]!["options"]!["captured_model_metadata"]!["model"] = "other"; break;
            case "elapsed": run["session"]!["request"]!["options"]!["llm_budget"]!["max_elapsed_ms"] = 1; break;
            case "threshold": run["session"]!["request"]!["options"]!.AsObject().Remove("optimization_threshold_policy"); break;
            case "strict_authority":
                run["session"]!["request"]!["options"]!.AsObject().Remove("optimization_threshold_policy");
                var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
                policy.Remove("optimization_threshold_policy");
                await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
                var transport = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
                transport["effective_transport_policy"]!.AsObject().Remove("optimization_threshold_policy");
                transport["effective_transport_policy_hash"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(transport["effective_transport_policy"]!.ToJsonString());
                await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, transport, Ct); break;
            case "status": run["session"]!["status"] = PlanningStatus.Generating; break;
            case "execution": run["execution_started"] = "issued"; break;
            case "tools": run["session"]!["pendingCall"]!["request"]!["tools"] = new JsonArray(new JsonObject()); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new JsonObject(), Ct); break;
            case "usage":
            case "body":
                var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
                if (defect == "usage") journal["usage"] = new JsonObject();
                else journal["transport"]!["Attempts"]![0]!["Body"] = "partial";
                await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct); break;
            case "failure": await campaign.SaveAsync("planning-evaluation-failures", RequestId, new JsonObject(), Ct); break;
            case "partial_body":
            case "transport_failure":
            case "unmatched_cancellation":
            case "complete_body":
                var cancelledJournal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
                var attempt = cancelledJournal["transport"]!["Attempts"]![0]!;
                attempt["Failure"] = defect == "transport_failure" ? "transport" : "cancelled";
                attempt["RetryDecision"] = defect == "unmatched_cancellation" ? "other" : "cancelled; no retry";
                attempt["ResponseBodyComplete"] = defect == "complete_body";
                if (defect == "partial_body") attempt["PartialBody"] = "partial received bytes";
                await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, cancelledJournal, Ct); break;
        }
        await campaign.SaveAsync("planning-evaluation-runs", CancelledRun, run, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", CancelledRun,
            RequestId, Ct, BenchmarkCampaign.CancelledPlanningRetention));
        Assert.Equal(before, records.Snapshot());
    }

    private static async Task<BenchmarkCampaign> SeedCancelledServerAsync(GnOuGo.KeyVault.Core.Services.IKeyVaultRecordStore records)
    {
        var campaign = await SeedCapturedRetriesAsync(records, 1, true);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, Ct))!;
        run["session"]!["status"] = PlanningStatus.Cancelled;
        run["session"]!["request"]!["options"]!["captured_model_metadata"] = new JsonObject { ["provider"] = "OpenAi", ["model"] = "private-deployment" };
        run["session"]!["pendingCall"]!["request"]!["provider"] = "OpenAi";
        run["session"]!["pendingCall"]!["request"]!["model"] = "private-deployment";
        run["result"]!["status"] = PlanningStatus.Cancelled;
        run["host"] = "GnOuGo.Agent.Server"; run["session_id"] = Session;
        run["provider"] = "OpenAi"; run["model"] = "private-deployment";
        run["campaign_configuration"] = new string('c', 64);
        await campaign.SaveAsync("planning-evaluation-configuration", "configuration:" + new string('c', 64), new()
        { ["configuration"] = new JsonObject { ["provider"] = "OpenAi", ["model"] = "private-deployment" } }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", RequestId, run["session"]!["pendingCall"]!["request"]!.DeepClone().AsObject(), Ct);
        run["session"]!["request"]!["options"]!.AsObject().Remove("benchmark_planning_elapsed_ms");
        var policy = (await campaign.LoadAsync("planning-evaluation-session-policy", Session, Ct))!;
        run["session"]!["request"]!["options"]!["optimization_threshold_policy"] = "diagnostic-v1";
        policy["optimization_threshold_policy"] = "diagnostic-v1";
        policy["elapsed_limit_origin"] = "captured_server_session_budget";
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, policy, Ct);
        await campaign.SaveAsync("planning-evaluation-runs", CancelledRun, run, Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Attempts"]![0]!["Failure"] = null;
        journal["effective_transport_policy"]!["optimization_threshold_policy"] = "diagnostic-v1";
        journal["effective_transport_policy"]!["token_threshold_policy"] = "advisory";
        journal["effective_transport_policy_hash"] = GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(journal["effective_transport_policy"]!.ToJsonString());
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, new()
        { ["stage"] = "dispatch", ["exception_type"] = "OperationCanceledException" }, Ct);
        return campaign;
    }
}
