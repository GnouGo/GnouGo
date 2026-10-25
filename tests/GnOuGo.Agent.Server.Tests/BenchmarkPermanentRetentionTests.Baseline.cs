using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    private static JsonObject BaselineFixture() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "BaselineRetention", "baseline-serializer-fixture.json")))!.AsObject();

    [Fact]
    public void ActualLegacySerializerPreservesOriginalAndFinalDispatchHashDomains()
    {
        var fixture = BaselineFixture();
        var request = fixture["original_request"]!.DeepClone().AsObject();
        Assert.Equal(fixture["original_request_text"]!.ToString(), request.ToJsonString());
        Assert.False(request.ContainsKey("requiredReviewCalls"));
        Assert.True(request["useBackgroundMode"]!.GetValue<bool>());
        var modern = request.Deserialize(PlanningJsonContext.Default.LLMRequest)!;
        Assert.Equal(fixture["original_request_sha256"]!.ToString(), PlanningGraphCompiler.Fingerprint(
            JsonSerializer.Serialize(modern, PlanningJsonContext.Default.LLMRequest)));
        var copied = request.DeepClone().AsObject();
        copied["provider"] = "fixture"; copied["model"] = "pinned"; copied["useBackgroundMode"] = false; copied["disableTransportRetries"] = true;
        Assert.Equal(fixture["dispatch_request_text"]!.ToString(), copied.ToJsonString());
        Assert.Equal(fixture["dispatch_request_sha256"]!.ToString(), PlanningGraphCompiler.Fingerprint(copied.ToJsonString()));
        Assert.Equal(fixture["original_request_text"]!.ToString(), request.ToJsonString());
        Assert.False(fixture["factory_request"]!["disableTransportRetries"]!.GetValue<bool>());
    }

    [Fact]
    public void LegacyTransportProofUsesOnlyTheActualSingleAttemptTransformation()
    {
        var (state, request, policy, journal, retry) = BaselinePolicy();
        var original = request.ToJsonString();
        BenchmarkFrozenBaselineArm.ValidateTransport(state, request, policy, journal, retry, "fixture", "pinned");
        Assert.Equal(original, request.ToJsonString());
        Assert.Equal(1, journal["effective_transport_policy"]!["effective_retry_policy"]!["MaxUncertainRetries"]!.GetValue<int>());
        Assert.Equal(4, retry["MaxAttempts"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("construction")]
    [InlineData("core_policy")]
    [InlineData("session_core_policy")]
    [InlineData("presentation")]
    [InlineData("required_reviews")]
    [InlineData("accounting")]
    [InlineData("headroom")]
    [InlineData("version")]
    [InlineData("runtime_mode")]
    [InlineData("sdk_mode")]
    [InlineData("retries")]
    [InlineData("background")]
    [InlineData("recovery")]
    [InlineData("request_hash")]
    [InlineData("dispatch_hash")]
    [InlineData("policy_hash")]
    [InlineData("max_attempts")]
    [InlineData("uncertain")]
    [InlineData("timeout")]
    [InlineData("backoff")]
    [InlineData("provider")]
    [InlineData("model")]
    public void LegacyTransportRejectsSingleFieldSubstitutions(string mutation)
    {
        var (state, request, policy, journal, retry) = BaselinePolicy();
        var effective = journal["effective_transport_policy"]!.AsObject(); var provider = "fixture"; var model = "pinned";
        switch (mutation)
        {
            case "construction": state.Construction!.Version = 2; break;
            case "core_policy": state.Request.Options["planning_transport_policy"] = new JsonObject(); break;
            case "session_core_policy": policy["planning_transport_policy"] = new JsonObject(); break;
            case "presentation": policy["construction_presentation_version"] = 6; break;
            case "required_reviews": request["requiredReviewCalls"] = null; break;
            case "accounting": policy["http_usage_accounting_version"] = 1; break;
            case "headroom": policy["construction_review_headroom"] = 4; break;
            case "version": effective["version"] = 1; break;
            case "runtime_mode": effective["mode"] = "runtime_single_attempt"; break;
            case "sdk_mode": effective["mode"] = "sdk_single_attempt"; break;
            case "retries": effective["disable_transport_retries"] = false; break;
            case "background": effective["use_background_mode"] = true; break;
            case "recovery": effective["allow_http_recovery"] = true; break;
            case "request_hash": effective["request_hash"] = new string('0', 64); break;
            case "dispatch_hash": effective["dispatch_request_hash"] = BaselineFixture()["factory_request_sha256"]!.DeepClone(); break;
            case "max_attempts": effective["effective_retry_policy"]!["MaxAttempts"] = 2; break;
            case "uncertain": effective["effective_retry_policy"]!["MaxUncertainRetries"] = 0; break;
            case "timeout": effective["effective_retry_policy"]!["AttemptTimeoutMilliseconds"] = 1; break;
            case "backoff": effective["effective_retry_policy"]!["BaseDelayMilliseconds"] = 0; break;
            case "provider": provider = "different"; break;
            case "model": model = "different"; break;
        }
        journal["effective_transport_policy_hash"] = mutation == "policy_hash" ? new string('0', 64) : PlanningGraphCompiler.Fingerprint(effective.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => BenchmarkFrozenBaselineArm.ValidateTransport(state, request, policy, journal, retry, provider, model));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("/unused/proof.json", null, true)]
    [InlineData("/unused/proof.json", "not-a-hash", true)]
    public void FrozenProofRequiresAnIndependentExpectedHash(string? path, string? hash, bool invalid)
    {
        if (invalid) Assert.Throws<InvalidOperationException>(() => BenchmarkFrozenBaselineArm.FromArguments(path, hash));
        else Assert.Null(BenchmarkFrozenBaselineArm.FromArguments(path, hash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AModeLabelOrChangedProofCannotBypassOriginalOperatorGuards(bool matchingHash)
    {
        var directory = Directory.CreateTempSubdirectory("baseline-retention-proof-");
        try
        {
            var path = Path.Combine(directory.FullName, "proof.json");
            const string json = "{\"version\":1,\"mode\":\"baseline_incremental_single_attempt\"}";
            await File.WriteAllTextAsync(path, json, Ct);
            var proof = BenchmarkFrozenBaselineArm.FromArguments(path, matchingHash ? PlanningGraphCompiler.Fingerprint(json) : new string('0', 64));
            var records = new Records(); var campaign = await SeedAsync(records);
            var before = records.Snapshot(); var writes = records.Writes;
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync(
                SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, Ct, frozenBaseline: proof));
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ApplyPermanentRetentionAsync(
                SchemaPortabilityCampaign.Collection, "run:" + Session, RequestId, new string('a', 64), Operator, Authorization, Ct, frozenBaseline: proof));
            Assert.Equal(before, records.Snapshot()); Assert.Equal(writes, records.Writes);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void FrozenClosureReadbackRequiresTheSameProofWhileHistoricalClosuresStayUnchanged()
    {
        BenchmarkFrozenBaselineArm.RequireSame(null, null);
        var proof = new JsonObject { ["proof_sha256"] = new string('a', 64), ["source"] = new string('b', 40) };
        var retained = proof.DeepClone();
        BenchmarkFrozenBaselineArm.RequireSame(retained, proof);
        Assert.Throws<InvalidOperationException>(() => BenchmarkFrozenBaselineArm.RequireSame(retained, null));
        Assert.Throws<InvalidOperationException>(() => BenchmarkFrozenBaselineArm.RequireSame(null, proof));
        proof["proof_sha256"] = new string('c', 64);
        Assert.Throws<InvalidOperationException>(() => BenchmarkFrozenBaselineArm.RequireSame(retained, proof));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("source")]
    [InlineData("campaign")]
    [InlineData("collection")]
    [InlineData("run_key")]
    [InlineData("session")]
    [InlineData("case")]
    [InlineData("cohort")]
    [InlineData("repetition")]
    [InlineData("configuration")]
    [InlineData("input")]
    [InlineData("retry")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("campaign_configuration")]
    [InlineData("after_dispatch")]
    public void FrozenRunBindingRejectsChangedOriginalIdentities(string? mutation)
    {
        // Unit test of bindings after file/source validation, not a fabricated eligible arm.
        // No source/build proof or operator authorization is asserted by this fixture.
        var (state, request, policy, journal, retry) = BaselinePolicy();
        const string source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        state.Request.SessionId = "baseline-collections-1";
        state.PendingCall!.Id = state.Request.SessionId + ":1:original";
        state.PendingCall.Request.ClientRequestId = state.PendingCall.Id;
        request["clientRequestId"] = state.PendingCall.Id;
        var effective = journal["effective_transport_policy"]!.AsObject();
        effective["request_hash"] = PlanningGraphCompiler.Fingerprint(request.ToJsonString());
        var dispatch = request.DeepClone().AsObject(); dispatch["provider"] = "fixture"; dispatch["model"] = "pinned";
        dispatch["useBackgroundMode"] = false; dispatch["disableTransportRetries"] = true;
        effective["dispatch_request_hash"] = PlanningGraphCompiler.Fingerprint(dispatch.ToJsonString());
        journal["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(effective.ToJsonString());
        var originalRequest = BaselineFixture()["session"]!["request"]!.DeepClone().AsObject();
        var configuration = PlanningBenchmarkMeasurements.Configuration(state.Request);
        configuration["policy"] = originalRequest["policy"]!.DeepClone();
        var campaignConfiguration = new JsonObject { ["provider"] = "fixture", ["model"] = "pinned", ["endpoint"] = "https://invalid.example" };
        var binding = new JsonObject { ["campaign"] = "retention", ["run_collection"] = SchemaPortabilityCampaign.Collection,
            ["run_key"] = "run:" + state.Request.SessionId, ["session_id"] = state.Request.SessionId, ["case"] = state.Request.Name,
            ["cohort"] = "baseline", ["repetition"] = 1, ["configuration"] = configuration,
            ["rendered_input_sha256"] = PlanningGraphCompiler.Fingerprint(state.Request.Prompt), ["retry_policy"] = retry.DeepClone(),
            ["provider"] = "fixture", ["model"] = "pinned",
            ["campaign_configuration_sha256"] = PlanningGraphCompiler.Fingerprint(campaignConfiguration.ToJsonString()) };
        var firstRequest = DateTimeOffset.UtcNow;
        var proof = new JsonObject { ["source"] = source, ["proof_sha256"] = new string('b', 64),
            ["reviewed_at_utc"] = firstRequest.AddMinutes(-1).ToString("O"), ["binding"] = binding,
            ["references"] = new JsonObject { ["freeze"] = new JsonObject { ["sha256"] = new string('c', 64) } } };
        switch (mutation)
        {
            case "source": proof["source"] = new string('d', 40); break;
            case "campaign": binding["campaign"] = "other"; break;
            case "collection": binding["run_collection"] = "other"; break;
            case "run_key": binding["run_key"] = "run:other"; break;
            case "session": binding["session_id"] = "other"; break;
            case "case": binding["case"] = "code"; break;
            case "cohort": binding["cohort"] = "changed"; break;
            case "repetition": binding["repetition"] = 2; break;
            case "configuration": configuration["calls"] = 99; break;
            case "input": binding["rendered_input_sha256"] = new string('0', 64); break;
            case "retry": binding["retry_policy"]!["MaxUncertainRetries"] = 0; break;
            case "provider": binding["provider"] = "other"; break;
            case "model": binding["model"] = "other"; break;
            case "campaign_configuration": campaignConfiguration["endpoint"] = "https://other.invalid"; break;
            case "after_dispatch": proof["reviewed_at_utc"] = firstRequest.ToString("O"); break;
        }
        JsonObject Validate() => BenchmarkFrozenBaselineArm.ValidateRun(proof, "retention", SchemaPortabilityCampaign.Collection,
            "run:" + state.Request.SessionId, source, state, originalRequest, request, policy, journal, retry, campaignConfiguration, firstRequest);
        if (mutation is not null) Assert.Throws<InvalidOperationException>(Validate);
        else
        {
            var bound = Validate();
            Assert.Equal(BenchmarkFrozenBaselineArm.Baseline, bound["baseline_source"]!.ToString());
            Assert.Equal(proof["proof_sha256"]!.ToString(), bound["proof_sha256"]!.ToString());
            Assert.Equal(source, bound["source"]!.ToString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("build_exit")]
    [InlineData("changed_source")]
    [InlineData("build_warnings")]
    [InlineData("status")]
    [InlineData("no_tests")]
    [InlineData("failed_tests")]
    [InlineData("skipped_tests")]
    [InlineData("failed_build")]
    [InlineData("diagnostic")]
    [InlineData("stage_exit")]
    [InlineData("stage_warnings")]
    [InlineData("inventory")]
    public void FrozenQualificationRequiresSuccessfulExecutedChecksOfTheSameInventory(string? mutation)
    {
        var inventory = new string('a', 64);
        var build = new JsonObject { ["exit_code"] = 0, ["source_unchanged"] = true,
            ["command"] = new JsonArray("dotnet", "build", "-warnaserror"), ["source_inventory_sha256"] = inventory };
        var validation = new JsonObject { ["status"] = "focused_and_build_passed", ["source_inventory_sha256"] = inventory,
            ["tests"] = new JsonObject { ["passed"] = 217, ["failed"] = 0, ["skipped"] = 0 },
            ["build"] = new JsonObject { ["passed"] = true, ["warnings_as_errors"] = true, ["diagnostics"] = new JsonArray() },
            ["stages"] = new JsonArray(
                new JsonObject { ["exit_code"] = 0, ["command"] = new JsonArray("dotnet", "test", "-warnaserror") },
                new JsonObject { ["exit_code"] = 0, ["command"] = new JsonArray("dotnet", "build", "-warnaserror") }) };
        switch (mutation)
        {
            case "build_exit": build["exit_code"] = 1; break;
            case "changed_source": build["source_unchanged"] = false; break;
            case "build_warnings": build["command"] = new JsonArray("dotnet", "build"); break;
            case "status": validation["status"] = "failed"; break;
            case "no_tests": validation["tests"]!["passed"] = 0; break;
            case "failed_tests": validation["tests"]!["failed"] = 1; break;
            case "skipped_tests": validation["tests"]!["skipped"] = 1; break;
            case "failed_build": validation["build"]!["passed"] = false; break;
            case "diagnostic": validation["build"]!["diagnostics"]!.AsArray().Add("retained diagnostic"); break;
            case "stage_exit": validation["stages"]![1]!["exit_code"] = 1; break;
            case "stage_warnings": validation["stages"]![0]!["command"] = new JsonArray("dotnet", "test"); break;
            case "inventory": validation["source_inventory_sha256"] = new string('b', 64); break;
        }
        if (mutation is null) BenchmarkFrozenBaselineArm.ValidateQualification(build, validation);
        else Assert.Throws<InvalidOperationException>(() => BenchmarkFrozenBaselineArm.ValidateQualification(build, validation));
    }

    private static (PlanningSession State, JsonObject Request, JsonObject Policy, JsonObject Journal, JsonObject Retry) BaselinePolicy()
    {
        var fixture = BaselineFixture();
        var effective = new JsonObject { ["version"] = 2, ["mode"] = "baseline_incremental_single_attempt",
            ["disable_transport_retries"] = true, ["use_background_mode"] = false, ["allow_http_recovery"] = false,
            ["request_hash"] = fixture["original_request_sha256"]!.DeepClone(), ["dispatch_request_hash"] = fixture["dispatch_request_sha256"]!.DeepClone(),
            ["effective_retry_policy"] = fixture["effective_retry_policy"]!.DeepClone() };
        return (fixture["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!, fixture["original_request"]!.DeepClone().AsObject(),
            new() { ["http_usage_accounting_version"] = 2, ["construction_review_headroom"] = 2 },
            new() { ["effective_transport_policy"] = effective, ["effective_transport_policy_hash"] = PlanningGraphCompiler.Fingerprint(effective.ToJsonString()) },
            fixture["full_retry_policy"]!.DeepClone().AsObject());
    }
}
