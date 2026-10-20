using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkPermanentRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Session = "uncertain-session", RequestId = Session + ":1:original";
    private const string Operator = "authorized-benchmark-operator", Authorization = "Permanently retain this exact inconclusive run and its full reservation.";
    private const string Closures = "planning-evaluation-closures";
    private const string Http502 = BenchmarkCampaign.SingleHttp502Retention;
    private const string ErrorBody = """{"errorCode":"upstream_failure","errorMessage":"Provider unavailable.","errorDetail":"No completion receipt."}""";

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, Http502)]
    [InlineData(true, true, Http502)]
    public async Task ExplicitRetentionPreservesEvidenceAndAccountingWhilePermanentlyBlockingTheRun(bool capturedTransport, bool corpus, string? kind)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport, corpus, kind);
        var collection = corpus ? "planning-evaluation-runs" : SchemaPortabilityCampaign.Collection;
        var key = corpus ? "source:pilot:case:1" : "run:" + Session;
        var original = records.Snapshot(); var writes = records.Writes;
        var proposal = await campaign.InspectPermanentRetentionAsync(collection, key, RequestId, Ct, kind);
        Assert.Equal(writes, records.Writes);
        Assert.Equal(kind is not null, proposal["evidence"]!.AsObject().ContainsKey("retention_kind"));
        Assert.Equal(kind, proposal["evidence"]!["retention_kind"]?.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(RequestId, Ct));
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(1m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        var hash = proposal["evidence_hash"]!.ToString();
        var closure = await campaign.ApplyPermanentRetentionAsync(collection, key, RequestId, hash, Operator, Authorization, Ct, kind);
        Assert.Equal("inconclusive", closure["outcome"]!.ToString());
        Assert.Equal(writes + 1, records.Writes);
        var retainedInspection = await campaign.InspectPermanentRetentionAsync(collection, key, RequestId, Ct, kind);
        Assert.True(retainedInspection["already_retained"]!.GetValue<bool>());
        Assert.Equal(hash, retainedInspection["evidence_hash"]!.ToString());
        Assert.Equal(original, records.Snapshot(excludeClosures: true));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", RequestId, Ct));
        Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        var restarted = new BenchmarkCampaign(records, "retention");
        Assert.True(JsonNode.DeepEquals(closure, await restarted.ApplyPermanentRetentionAsync(collection, key, RequestId, hash, Operator, Authorization, Ct, kind)));
        Assert.Equal(writes + 1, records.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyPermanentRetentionAsync(collection, key, RequestId, hash, Operator, "different authority", Ct, kind));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InspectPermanentRetentionAsync(collection, key, RequestId, Ct, kind is null ? Http502 : null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyPermanentRetentionAsync(collection, key, RequestId, hash, Operator, Authorization, Ct, kind is null ? Http502 : null));
        var calls = 0;
        foreach (var id in new[] { RequestId, Session + ":2:new", Session + "-execution:1:new", Session + "-copilot:request" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallAsync(new() { ClientRequestId = id },
                _ => Task.CompletedTask, _ => { calls++; return Task.FromResult(new LLMResponse()); }, Ct, allowHttpRecovery: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RequireRunNotRetainedAsync(Session, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecoverPendingReceiptAsync(Session, 1, RequestId, 0, "new-source", true, Ct));
        Assert.Equal(0, calls);
        await restarted.CallAsync(new() { ClientRequestId = "independent:1:new" }, _ => Task.CompletedTask,
            _ => { calls++; return Task.FromResult(new LLMResponse()); }, Ct);
        Assert.Equal(1, calls);
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        var denied = new BenchmarkHttpJournal(restarted, "over-budget:1", 100, 20, 49.5m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.SaveAsync(new() { Attempts = [new() { Id = "new" }] }, Ct));
        await new BenchmarkCampaign(records, "another-campaign").RequireRunNotRetainedAsync(Session, Ct);
    }

    [Fact]
    public async Task Http502RequiresItsExplicitModeAndCannotUseHistoricalClosureOperators()
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
        var before = records.Snapshot(); var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainExhaustedRequestAsync(RequestId, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainSchemaRejectionAsync(RequestId, Ct));
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ApplyPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("single-http-500")]
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

    [Theory]
    [InlineData("status_200")]
    [InlineData("status_400")]
    [InlineData("status_500")]
    [InlineData("status_503")]
    [InlineData("status_504")]
    [InlineData("failure_status")]
    [InlineData("failure_kind")]
    [InlineData("failure_count")]
    [InlineData("not_exhausted")]
    [InlineData("not_retryable")]
    [InlineData("attempt_failure")]
    [InlineData("missing_body")]
    [InlineData("zero_attempts")]
    [InlineData("mixed_attempts")]
    [InlineData("usage")]
    [InlineData("receipt")]
    [InlineData("missing_policy")]
    [InlineData("policy_hash")]
    [InlineData("recovery_enabled")]
    [InlineData("retries_enabled")]
    [InlineData("multiple_allowed_attempts")]
    public async Task Http502RequiresExactFailureAndCapturedSingleAttemptPolicy(string defect)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", RequestId, Ct))!;
        var attempt = journal["transport"]!["Attempts"]![0]!;
        var policy = journal["effective_transport_policy"]!.AsObject();
        if (defect.StartsWith("status_", StringComparison.Ordinal))
        {
            var status = int.Parse(defect["status_".Length..], System.Globalization.CultureInfo.InvariantCulture);
            attempt["Status"] = status; failure["status_code"] = status;
        }
        switch (defect)
        {
            case "failure_status": failure["status_code"] = 503; break;
            case "failure_kind": failure["kind"] = "Timeout"; break;
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
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData("""{"error":{"message":"Unavailable","type":"server_error","code":null,"param":null}}""", true)]
    [InlineData(ErrorBody, true)]
    [InlineData("""{"error":{"message":"Unavailable"},"usage":{"input_tokens":1}}""", false)]
    [InlineData("""{"error":{"message":"Unavailable"},"usage":null}""", false)]
    [InlineData("""{"error":{"message":"Unavailable","usage":null}}""", false)]
    [InlineData("""{"error":{"message":"Unavailable"},"id":"response_1"}""", false)]
    [InlineData("""{"choices":[{"message":{"content":"Completed"}}]}""", false)]
    [InlineData("""{"error":{"message":"Unavailable"},"output":[]}""", false)]
    [InlineData("""{"errorCode":"failed","errorMessage":"Unavailable","errorDetail":{"usage":1}}""", false)]
    [InlineData("""{"errorCode":"failed","errorMessage":"Unavailable","errorDetail":"","usage":null}""", false)]
    [InlineData("""{"errorCode":"failed","errorMessage":"Unavailable","errorDetail":"","unknown":"value"}""", false)]
    [InlineData("""{"errorCode":"failed","errorMessage":"Unavailable","errorMessage":"duplicate"}""", false)]
    [InlineData("""{"error":{"message":"Unavailable","message":"duplicate"}}""", false)]
    [InlineData("""{"error":{"message":"Unavailable"},"error":{"message":"duplicate"}}""", false)]
    [InlineData("""{"error":{"message":"Unavailable","extra":null}}""", false)]
    [InlineData("""{"error":{"message":"Unavailable","code":{"usage":1}}}""", false)]
    [InlineData("""{"error":{"message":null}}""", false)]
    [InlineData("not JSON", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    public async Task Http502BodyMustBeBoundedRecognizedErrorOnlyEvidence(string body, bool eligible)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Attempts"]![0]!["Body"] = body;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var before = records.Snapshot(); var writes = records.Writes;
        if (eligible)
            Assert.NotNull(await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
                SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public async Task Http502OversizedErrorBodyIsRefusedWithoutTruncation()
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["transport"]!["Attempts"]![0]!["Body"] = new JsonObject
            { ["error"] = new JsonObject { ["message"] = new string('x', 65_536) } }.ToJsonString();
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        var before = records.Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
            SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502));
        Assert.Equal(before, records.Snapshot());
    }

    [Theory]
    [InlineData("body")]
    [InlineData("policy")]
    [InlineData("authorization")]
    [InlineData("hash")]
    [InlineData("mode")]
    public async Task ChangedHttp502EvidenceOrAuthorityRejectsApplyWithoutWrites(string change)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, Http502);
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
            change == "authorization" ? "" : Authorization, Ct, change == "mode" ? null : Http502));
        Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public async Task CliHttp502InspectionNeedsExplicitModeAndDoesNotCreateALeaseOrClosure()
    {
        var root = Directory.CreateTempSubdirectory("retention-cli-").FullName;
        try
        {
            var records = KeyVaultBenchmarkModel.WorkspaceRecords(root);
            var campaign = await SeedAsync(records, capturedTransport: true, retentionKind: Http502);
            string[] args = ["--schema-portability", "retain-uncertain-request", "--workspace", root, "--campaign", "retention",
                "--run-key", "run:" + Session, "--request-id", RequestId];
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaPortabilityCampaign.RunAsync(args));
            await SchemaPortabilityCampaign.RunAsync([.. args, "--retention-kind", Http502]);
            Assert.False(File.Exists(Path.Combine(root, ".GnOuGo", "data", "planning-evaluation", "retention.lock")));
            Assert.Null(await campaign.LoadAsync(Closures, RequestId, Ct));
            Assert.Equal(1m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("active")]
    [InlineData("executed")]
    [InlineData("execution_result")]
    [InlineData("tool")]
    [InlineData("receipt")]
    [InlineData("http_success")]
    [InlineData("response_body")]
    [InlineData("usage")]
    [InlineData("missing_failure")]
    [InlineData("wrong_count")]
    [InlineData("not_exhausted")]
    [InlineData("cancelled")]
    [InlineData("zero_attempts")]
    [InlineData("two_attempts")]
    [InlineData("missing_reservation")]
    [InlineData("wrong_request")]
    [InlineData("wrong_pending")]
    [InlineData("wrong_tenant")]
    [InlineData("missing_source")]
    [InlineData("missing_no_replay_policy")]
    [InlineData("replayable_transport")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("approved")]
    [InlineData("yaml")]
    [InlineData("input_under_reserved")]
    [InlineData("output_under_reserved")]
    [InlineData("quote_missing")]
    [InlineData("policy_mismatch")]
    [InlineData("manifest_mismatch")]
    [InlineData("transport_hash")]
    public async Task IneligibleEvidenceRejectsWithoutWriting(string defect)
    {
        var records = new Records(); var campaign = await SeedAsync(records, capturedTransport: true);
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
        await Assert.ThrowsAnyAsync<Exception>(() => campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct));
        Assert.Equal(writes, records.Writes); Assert.Equal(snapshot, records.Snapshot());
    }

    [Theory]
    [InlineData("run")]
    [InlineData("failure")]
    [InlineData("accounting")]
    [InlineData("authorization")]
    [InlineData("hash")]
    public async Task ChangedEvidenceOrMissingAuthorityRejectsApplyWithoutWrites(string change)
    {
        var records = new Records(); var campaign = await SeedAsync(records);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct);
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
            change == "hash" ? new string('0', 64) : proposal["evidence_hash"]!.ToString(), Operator, change == "authorization" ? "" : Authorization, Ct));
        Assert.Equal(writes, records.Writes); Assert.Equal(snapshot, records.Snapshot());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, Http502)]
    [InlineData(true, Http502)]
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

    [Fact]
    public async Task ForgedClosureBindingsFailClosedAndCannotBeOverwritten()
    {
        var records = new Records(); var campaign = await SeedAsync(records);
        var proposal = await campaign.InspectPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct);
        var hash = proposal["evidence_hash"]!.ToString();
        var closure = await campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct);
        closure["evidence"]!["session_id"] = "forged";
        await campaign.SaveAsync(Closures, RequestId, closure, Ct);
        var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RequireRunNotRetainedAsync(Session, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ApplyPermanentRetentionAsync(SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, hash, Operator, Authorization, Ct));
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
    [InlineData(Http502)]
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

    private static JsonObject TransportPolicy() => new()
    {
        ["version"] = 1, ["disable_transport_retries"] = true, ["allow_http_recovery"] = false,
        ["effective_retry_policy"] = new JsonObject { ["MaxAttempts"] = 1, ["MaxUncertainRetries"] = 1 }
    };

    private static async Task<BenchmarkCampaign> SeedAsync(IKeyVaultRecordStore records, bool capturedTransport = false, bool corpus = false,
        string? retentionKind = null)
    {
        var campaign = new BenchmarkCampaign(records, "retention");
        var request = new LLMRequest { ClientRequestId = RequestId, Prompt = "Original exact intent.", MaxTokens = 8192,
            StructuredOutputSchema = new JsonObject { ["title"] = "construction_intent", ["type"] = "object" } };
        var state = new PlanningSession { Request = new() { TenantId = "benchmark", SessionId = Session }, Revision = 1, IntentVersion = 3,
            Status = PlanningStatus.Stopped, ModelCalls = 1, Construction = new() { Version = 2 },
            PendingCall = new() { Id = RequestId, Purpose = "construction_intent", Request = request } };
        state.Diagnostics.Add(new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Completion remains unknown."));
        await campaign.SaveAsync("planning-evaluation-requests", RequestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var journal = new BenchmarkHttpJournal(campaign, RequestId, 6651, 8192, 1m, 64, effectiveTransportPolicy: capturedTransport ? TransportPolicy() : null);
        await journal.PrepareAsync(Ct, new JsonObject { ["source_currency"] = "USD", ["target_currency"] = "EUR", ["rate"] = .9m,
            ["source"] = "retained-test-authority", ["as_of_utc"] = DateTimeOffset.UtcNow.AddDays(-1) });
        var transport = new LLMHttpRetryState { Fingerprint = "original-http", Attempts = [new() { Id = RequestId + ":attempt",
            Failure = retentionKind is null ? "timeout" : null }] };
        await journal.SaveAsync(transport, Ct);
        if (retentionKind is not null)
        {
            transport.Attempts[0].Status = 502; transport.Attempts[0].Body = ErrorBody;
            await journal.SaveAsync(transport, Ct);
        }
        var failure = new JsonObject
        {
            ["stage"] = "dispatch", ["kind"] = retentionKind is null ? "Timeout" : "ServiceUnavailable",
            ["retryable"] = true, ["attempt_count"] = 1, ["retry_exhausted"] = true
        };
        if (retentionKind is not null) failure["status_code"] = 502;
        await campaign.SaveAsync("planning-evaluation-failures", RequestId, failure, Ct);
        await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", new() { ["MaxAttempts"] = 4, ["MaxUncertainRetries"] = 1 }, Ct);
        await campaign.SaveAsync("planning-evaluation-session-policy", Session, new() { ["construction_review_headroom"] = 2, ["construction_version"] = 2,
            ["max_physical_attempts"] = state.Request.MaxModelCalls, ["max_input_tokens"] = state.Request.Generation.MaxInputTokensPerRequest,
            ["max_output_tokens"] = state.Request.Generation.MaxOutputTokens, ["max_corrections"] = state.Request.MaxReplanAttempts }, Ct);
        var run = new JsonObject { ["source"] = new string('a', 40), ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession),
            ["result"] = new JsonObject { ["status"] = "stopped", ["execution_status"] = "not_started", ["execution_correct"] = false } };
        await campaign.SaveAsync(corpus ? "planning-evaluation-runs" : SchemaPortabilityCampaign.Collection, corpus ? "source:pilot:case:1" : "run:" + Session, run, Ct);
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
