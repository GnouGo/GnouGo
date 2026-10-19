using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactRequestMeasurementTests
{
    [Fact]
    public void ProviderCursorMetadataCountsAsDescriptorsAndReconcilesExactEnvelope()
    {
        var providers = new JsonObject { ["version"] = new string('a', 64), ["offset"] = 2, ["total"] = 3,
            ["items"] = new JsonArray(new JsonObject { ["sourceId"] = "source/é", ["cursor"] = "opaque:\"next\"/🙂", ["available"] = true }), ["next"] = null };
        var context = new JsonObject { ["providerRequests"] = providers };
        var request = new LLMRequest { Prompt = "Choose only an issued provider pair.\n" + PlanningJsonTransport.Prompt(context) };
        var original = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var measurement = PlanningRequestMeasurements.Measure(request, "construction_discovery", 3000);
        var rendered = PlanningJsonTransport.Prompt(context);
        var expected = JsonEncodedText.Encode(rendered[1..^1]).EncodedUtf8Bytes.Length;
        Assert.Equal(expected, Assert.Single(measurement.Components, c => c.Component == "mcp_descriptors").SerializedBytes);
        Assert.DoesNotContain(measurement.Components, c => c.Component == "construction_context");
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(c => c.SerializedBytes));
        Assert.Equal(original, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal("opaque:\"next\"/🙂", providers["items"]![0]!["cursor"]!.ToString());
    }

    [Fact]
    public void BenchmarkCapturesEffectiveLimitsBeforeItsFirstCheckpoint()
    {
        var state = new PlanningSession();
        PlanningBenchmarkMeasurements.ConfigureRequest(state.Request, ["--max-input-tokens", "2000"], retained: false);
        PlanningBenchmarkMeasurements.CaptureConstructionSettings(state);
        var limits = state.Construction!.Limits.InputLimits!;
        Assert.Equal(2000, limits.Selection);
        Assert.Equal(2000, limits.Draft);
        Assert.Equal(2000, limits.Review);
        var saved = state.Request.Options["construction_input_limits"]!.ToJsonString();
        PlanningBenchmarkMeasurements.ConfigureRequest(state.Request, [], retained: true);
        Assert.Equal(saved, state.Request.Options["construction_input_limits"]!.ToJsonString());
    }

    [Theory]
    [InlineData("construction_intent", 3000)]
    [InlineData("construction_select", 3000)]
    [InlineData("construction_discovery", 3000)]
    [InlineData("construction_draft", 8000)]
    [InlineData("replan", 8000)]
    [InlineData("construction_local_judge", 4000)]
    [InlineData("construction_intermediate_judge", 4000)]
    public void CapturedCompactPhaseLimitsCannotBeRaisedByGeneralAllowance(string phase, int expected)
    {
        var request = CompactRequest();
        Assert.Equal(expected, PlanningConstructionInputPolicy.EffectiveLimit(request, phase));
        request.Generation.MaxInputTokensPerRequest = 1000;
        Assert.Equal(1000, PlanningConstructionInputPolicy.EffectiveLimit(request, phase));
        request.Generation.MaxInputTokensPerRequest = 128000;
        request.Options["construction_input_limits"] = new JsonObject { ["selection"] = 9000, ["draft"] = 9000, ["review"] = 9000 };
        Assert.Equal(expected, PlanningConstructionInputPolicy.EffectiveLimit(request, phase));
        request.Options["construction_input_limits"] = new JsonObject { ["selection"] = 700, ["draft"] = 700, ["review"] = 700 };
        Assert.Equal(700, PlanningConstructionInputPolicy.EffectiveLimit(request, phase));
    }

    [Fact]
    public void HistoricalRequestsRetainTheirAllowanceAndCompactUnknownPhasesFailClosed()
    {
        var historical = JsonSerializer.Deserialize("{}", PlanningJsonContext.Default.PlanningRequest)!;
        Assert.Equal(8, historical.MaxModelCalls);
        Assert.Equal(12000, PlanningConstructionInputPolicy.EffectiveLimit(historical, "old-purpose"));
        var compact = CompactRequest();
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionInputPolicy.EffectiveLimit(compact, "old-purpose"));
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionInputPolicy.EffectiveLimit(compact, new LLMRequest()));
    }

    [Fact]
    public void ComponentsPartitionExactEscapedEnvelopeAndDoNotChangeRequestOrApprovalIdentity()
    {
        var request = new LLMRequest { ClientRequestId = "identity", Prompt = "Instructions é < & \"quoted\".\n" +
            PlanningJsonTransport.Prompt(new JsonObject { ["request"] = "Original intention 🙂", ["requirements"] = new JsonArray("A\\B"),
                ["baseHash"] = "baseline", ["instructions"] = "Retained host policy", ["step"] = new JsonObject { ["objective"] = "Immediate objective", ["scope"] = "/root" },
                ["operations"] = new JsonArray(new JsonObject { ["id"] = "operation", ["version"] = "v1", ["hash"] = "contract-hash", ["schema"] = new string('x', 150) }),
                ["interfaces"] = new JsonArray("input"), ["draft"] = new JsonObject() }),
            StructuredOutputSchema = PlanningSchemas.Object(("result", PlanningSchemas.String())), Model = "model", MaxTokens = 8192 };
        var before = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var measurement = PlanningRequestMeasurements.Measure(request, "construction_draft", 8000, "step");
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(c => c.SerializedBytes));
        Assert.Equal((System.Text.Encoding.UTF8.GetByteCount(before) + 2L) / 3 + 256, measurement.EstimatedInputTokens);
        Assert.Equal(before, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.Contains(measurement.Components, c => c.Component == "mcp_contracts" && c.SerializedBytes > 150);
        Assert.Contains(measurement.Components, c => c.Component == "response_schema" && c.SerializedBytes > 0);
        Assert.Contains(measurement.Components, c => c.Component == "intent" && c.SerializedBytes > 0);
        Assert.Contains(measurement.Components, c => c.Component == "objective" && c.SerializedBytes > 0);
        Assert.Equal("baseline", measurement.BaselineHash);
        Assert.Equal("contract-hash", Assert.Single(measurement.Contracts).Hash);
        Assert.Equal("v1", Assert.Single(measurement.Contracts).Version);
        Assert.Equal(64, measurement.SchemaHash.Length);
        var state = new PlanningSession { Yaml = "artifact" };
        var artifact = state.ComputeArtifactHash();
        state.RequestMeasurements = [measurement];
        Assert.Equal(artifact, state.ComputeArtifactHash());
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal(measurement.RequestHash, Assert.Single(restored.RequestMeasurements!).RequestHash);
        Assert.DoesNotContain("Original intention", JsonSerializer.Serialize(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedCompleteRequestIsMeasuredAndCheckpointedBeforeDispatchWithoutCharge()
    {
        var state = new PlanningSession { Request = CompactRequest(), Construction = new() { Version = 2, Compact = new() } };
        var runtime = new TestRuntime();
        var schema = PlanningSchemas.Object(("value", PlanningSchemas.String()));
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            "construction_select", new string('x', 9000), schema, PlannerFixture.Ct, 8192));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Empty(runtime.Calls); Assert.Null(state.PendingCall); Assert.Equal(0, state.ModelCalls);
        var measurement = Assert.Single(state.RequestMeasurements!);
        Assert.Equal("rejected_before_dispatch", measurement.Admission);
        Assert.Equal(3000, measurement.InputLimit);
        Assert.True(measurement.EstimatedInputTokens > 3000);
        var run = new JsonObject { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) };
        var phase = PlanningBenchmarkMeasurements.RequestMetrics(run)["phases"]!["construction_select"]!;
        Assert.Equal(1, phase["rejected_before_dispatch"]!.GetValue<int>());
        Assert.Equal(1, phase["prepared_requests"]!.GetValue<int>());
        Assert.Equal(0, phase["requests"]!.GetValue<int>());
        Assert.Equal(measurement.CompleteRequestBytes, phase["peak_complete_request_bytes"]!.GetValue<long>());
        Assert.Equal(measurement.EstimatedInputTokens, phase["peak_estimated_input_tokens"]!.GetValue<long>());
        Assert.False(phase["within_captured_limits"]!.GetValue<bool>());
        Assert.True(phase["admitted_within_captured_limits"]!.GetValue<bool>());
        Assert.Null(phase["peak_admitted_input_tokens"]);
        Assert.Equal(measurement.CompleteRequestBytes, phase["components"]!.AsObject().Sum(c => c.Value!["serialized_bytes"]!.GetValue<long>()));
    }

    [Theory]
    [InlineData("scopeInterface")]
    [InlineData("group")]
    [InlineData("scopeGroup")]
    public void SelectedScopeAndGroupViewsCountAsInterfaces(string key)
    {
        var context = new JsonObject { [key] = new JsonObject { ["id"] = "selected", ["hash"] = "immutable-interface",
            ["inputs"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = "number" }),
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "result", ["schema"] = new JsonObject { ["type"] = "number" } }) } };
        var request = new LLMRequest { Prompt = "Review the selected typed interface.\n" + PlanningJsonTransport.Prompt(context) };
        var original = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var measurement = PlanningRequestMeasurements.Measure(request, "construction_local_judge", 4000);
        Assert.True(Assert.Single(measurement.Components, c => c.Component == "interfaces").SerializedBytes > 100);
        Assert.DoesNotContain(measurement.Components, c => c.Component == "construction_context");
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(c => c.SerializedBytes));
        Assert.Equal(original, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }

    [Fact]
    public void MixedAdmissionMetricsRetainRejectedPeaksAndAllComponentBytes()
    {
        var admitted = PlanningRequestMeasurements.Measure(new() { Prompt = "Small discovery request." }, "construction_discovery", 3000);
        admitted.Admission = "admitted";
        var rejected = PlanningRequestMeasurements.Measure(new() { Prompt = new string('x', 9000) }, "construction_discovery", 3000);
        rejected.Admission = "rejected_before_dispatch";
        var first = JsonSerializer.SerializeToNode(admitted, PlanningJsonContext.Default.PlanningRequestMeasurement)!;
        var second = JsonSerializer.SerializeToNode(rejected, PlanningJsonContext.Default.PlanningRequestMeasurement)!;
        var run = new JsonObject { ["request_measurements"] = new JsonObject { [admitted.RequestHash] = first },
            ["session"] = new JsonObject { ["requestMeasurements"] = new JsonArray(first.DeepClone(), second) } };
        var report = PlanningBenchmarkMeasurements.RequestMetrics(run);
        var phase = report["phases"]!["construction_discovery"]!;
        Assert.Equal(2, report["requests"]!.AsArray().Count);
        Assert.Equal(2, phase["prepared_requests"]!.GetValue<int>());
        Assert.Equal(1, phase["requests"]!.GetValue<int>());
        Assert.Equal(1, phase["rejected_before_dispatch"]!.GetValue<int>());
        Assert.Equal(rejected.CompleteRequestBytes, phase["peak_complete_request_bytes"]!.GetValue<long>());
        Assert.Equal(rejected.EstimatedInputTokens, phase["peak_estimated_input_tokens"]!.GetValue<long>());
        Assert.Equal(admitted.EstimatedInputTokens, phase["peak_admitted_input_tokens"]!.GetValue<long>());
        Assert.False(phase["within_captured_limits"]!.GetValue<bool>());
        Assert.True(phase["admitted_within_captured_limits"]!.GetValue<bool>());
        Assert.Equal(admitted.CompleteRequestBytes + rejected.CompleteRequestBytes,
            phase["components"]!.AsObject().Sum(c => c.Value!["serialized_bytes"]!.GetValue<long>()));
    }

    [Theory]
    [InlineData("construction_select", "value")]
    [InlineData("construction_intent", "sourceId")]
    [InlineData("construction_discovery", "operationIds")]
    [InlineData("construction_draft", "extension")]
    [InlineData("construction_local_judge", "findings")]
    [InlineData("construction_intermediate_judge", "findings")]
    [InlineData("replan", "patch")]
    public async Task PendingRequestAndMeasurementRemainExactAcrossReceiptRecovery(string phase, string property)
    {
        var state = new PlanningSession { Status = PlanningStatus.Generating, Phase = phase, Request = CompactRequest(), Construction = new() { Version = 2, Compact = new() } };
        var physicalCalls = 0;
        var receipts = new Dictionary<string, LLMResponse>(StringComparer.Ordinal);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            if (receipts.TryGetValue(request.ClientRequestId!, out var receipt)) return receipt;
            physicalCalls++;
            receipt = new LLMResponse { Json = new JsonObject { [property] = "retained" } };
            receipts.Add(request.ClientRequestId!, receipt);
            return receipt;
        } };
        await PlanningModelCalls.CallAsync(state, runtime, phase, "Select one step.", PlanningSchemas.Object((property, PlanningSchemas.String())), PlannerFixture.Ct, 8192);
        state = PlannerFixture.Clone(Assert.Single(runtime.Checkpoints));
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var measurement = JsonSerializer.Serialize(Assert.Single(state.RequestMeasurements!), PlanningJsonContext.Default.PlanningRequestMeasurement);
        state.Request.Generation.MaxInputTokensPerRequest = 512;
        await PlanningModelCalls.CallAsync(state, runtime, phase, new string('x', 30000), new JsonObject(), PlannerFixture.Ct, 1);
        Assert.Equal(issued, JsonSerializer.Serialize(runtime.Calls.Last(), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(measurement, JsonSerializer.Serialize(Assert.Single(state.RequestMeasurements!), PlanningJsonContext.Default.PlanningRequestMeasurement));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(1, physicalCalls);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Null(state.PendingCall);
    }

    private static PlanningRequest CompactRequest() => new() { Options = new() { ["construction_version"] = 2 } };

    [Theory]
    [InlineData("complete", "passed")]
    [InlineData("missing", "inconclusive")]
    [InlineData("unknown", "inconclusive")]
    [InlineData("different_model", "inconclusive")]
    [InlineData("different_budget", "inconclusive")]
    [InlineData("unknown_cost", "inconclusive")]
    [InlineData("unsafe", "failed")]
    [InlineData("business_failed", "failed")]
    [InlineData("rejected_request", "failed")]
    [InlineData("duplicate_measurement", "passed")]
    [InlineData("legacy_phases", "passed")]
    [InlineData("judge_regression", "failed")]
    [InlineData("generation_regression", "failed")]
    public void CompactComparisonPermitsMoreCallsButRequiresIndependentCompleteBusinessEvidence(string defect, string expected)
    {
        JsonObject[] Arm(bool compact) => new[] { "collections", "amazon", "code" }.SelectMany(name => Enumerable.Range(1, 3).Select(repetition =>
        {
            var request = new PlanningRequest();
            PlanningBenchmarkMeasurements.ConfigureRequest(request, [], false);
            var draftLength = compact ? defect == "generation_regression" && name == "amazon" ? 6000 : 1000
                : defect == "generation_regression" && name == "collections" ? 10000 : 5000;
            var measurement = PlanningRequestMeasurements.Measure(new LLMRequest { Prompt = new string('a', draftLength) },
                !compact && defect == "legacy_phases" ? "tasks" : "construction_draft", compact ? 8000 : 32000);
            measurement.Admission = "admitted";
            var measurements = new JsonArray(JsonSerializer.SerializeToNode(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement));
            if (defect is "legacy_phases" or "judge_regression")
            {
                var judge = PlanningRequestMeasurements.Measure(new LLMRequest { Prompt = new string('j', compact ? defect == "judge_regression" && name == "amazon" ? 2000 : 500 : 1000) },
                    compact ? "construction_local_judge" : "judge", compact ? 4000 : 32000);
                judge.Admission = "admitted";
                measurements.Add(JsonSerializer.SerializeToNode(judge, PlanningJsonContext.Default.PlanningRequestMeasurement));
            }
            return new JsonObject
            {
                ["case"] = name, ["repetition"] = repetition, ["source_commit"] = compact ? "new" : "old", ["mode"] = "live",
                ["comparison_manifest"] = "same-producers-oracles-inputs", ["provider"] = "provider", ["model"] = "model", ["campaign"] = "retained",
                ["configuration"] = PlanningBenchmarkMeasurements.Configuration(request), ["independent_business_success"] = compact,
                ["safety_violations"] = new JsonArray(), ["calls"] = compact ? 20 : 2,
                ["input_tokens"] = compact ? 20_000 : 10_000, ["output_tokens"] = 500, ["estimated_cost_eur"] = 1m, ["elapsed_ms"] = 500,
                ["request_metrics"] = new JsonObject { ["requests"] = measurements }
            };
        })).ToArray();
        var before = Arm(false); var after = Arm(true);
        if (defect == "missing") after = after[..^1];
        if (defect == "unknown") after[0]["independent_business_success"] = null;
        if (defect == "different_model") after[0]["model"] = "another-model";
        if (defect == "different_budget") after[0]["configuration"]!["calls"] = 128;
        if (defect == "unknown_cost") after[0]["estimated_cost_eur"] = null;
        if (defect == "unsafe") after[0]["safety_violations"]!.AsArray().Add("unexpected-effect");
        if (defect == "business_failed") after[0]["independent_business_success"] = false;
        if (defect == "duplicate_measurement")
            after[0]["request_metrics"]!["requests"]!.AsArray().Add(after[0]["request_metrics"]!["requests"]![0]!.DeepClone());
        if (defect == "rejected_request")
        {
            var rejected = PlanningRequestMeasurements.Measure(new() { Prompt = new string('x', 9000) }, "construction_select", 3000);
            rejected.Admission = "rejected_before_dispatch";
            after[0]["request_metrics"]!["requests"]!.AsArray().Add(JsonSerializer.SerializeToNode(rejected, PlanningJsonContext.Default.PlanningRequestMeasurement));
        }
        var report = PlanningBenchmarkMeasurements.CompareCompactConstruction(before, after, ["collections", "amazon", "code"]);
        Assert.Equal(expected, report["status"]!.ToString());
        Assert.Equal(20m, report["candidate"]!["calls"]!["median"]!.GetValue<decimal>());
        Assert.Equal(2m, report["baseline"]!["calls"]!["median"]!.GetValue<decimal>());
        Assert.Equal(after.Length * 20m, report["candidate"]!["calls"]!["total"]!.GetValue<decimal>());
        Assert.Equal(after.Length * 500m, report["candidate"]!["elapsed_ms"]!["total"]!.GetValue<decimal>());
        Assert.Equal(60m, report["cases"]!["collections"]!["candidate"]!["calls"]!["total"]!.GetValue<decimal>());
        Assert.Equal(1500m, report["cases"]!["collections"]!["candidate"]!["elapsed_ms"]!["total"]!.GetValue<decimal>());
        var draftPhase = report["candidate"]!["request_phases"]!["construction_draft"]!;
        Assert.Equal(after.Length, draftPhase["prepared_requests"]!.GetValue<int>());
        Assert.Equal(after.Length, draftPhase["requests"]!.GetValue<int>());
        Assert.Equal(after.Length * (defect is "legacy_phases" or "judge_regression" ? 2 : 1), report["candidate"]!["request_count"]!.GetValue<int>());
        var totalDraftBytes = after.Sum(row => row["request_metrics"]!["requests"]![0]!["completeRequestBytes"]!.GetValue<long>());
        Assert.Equal(totalDraftBytes,
            draftPhase["components"]!.AsObject().Sum(component => component.Value!["serialized_bytes"]!.GetValue<long>()));
        Assert.Equal(3, report["cases"]!["collections"]!["candidate"]!["request_phases"]!["construction_draft"]!["prepared_requests"]!.GetValue<int>());
        var judgeComparison = report["cases"]!["amazon"]!["phase_comparisons"]!["judge"]!;
        Assert.Equal(defect == "judge_regression" ? "failed" : defect == "legacy_phases" ? "passed" : "not_comparable", judgeComparison["status"]!.ToString());
        if (defect == "judge_regression")
        {
            Assert.True(report["peak_request_reduction"]!.GetValue<double>() > 0);
            Assert.False(report["comparable_phase_families_improved"]!.GetValue<bool>());
            Assert.Equal("passed", report["cases"]!["collections"]!["phase_comparisons"]!["judge"]!["status"]!.ToString());
            Assert.Equal(4000, Assert.Single(judgeComparison["candidate_input_limits"]!.AsArray())!.GetValue<int>());
        }
        if (defect == "legacy_phases")
            Assert.Equal("passed", report["cases"]!["amazon"]!["phase_comparisons"]!["generation"]!["status"]!.ToString());
        if (defect == "generation_regression")
        {
            Assert.True(report["peak_request_reduction"]!.GetValue<double>() > 0);
            Assert.False(report["comparable_phase_families_improved"]!.GetValue<bool>());
            Assert.Equal("failed", report["cases"]!["amazon"]!["phase_comparisons"]!["generation"]!["status"]!.ToString());
            Assert.Equal("passed", report["cases"]!["collections"]!["phase_comparisons"]!["generation"]!["status"]!.ToString());
        }
        if (defect == "rejected_request")
        {
            Assert.False(report["within_captured_phase_limits"]!.GetValue<bool>());
            Assert.True(report["candidate"]!["peak_estimated_input_tokens"]!.GetValue<long>() > 3000);
            Assert.Equal(after.Length + 1, report["candidate"]!["prepared_request_count"]!.GetValue<int>());
            Assert.Equal(after.Length, report["candidate"]!["request_count"]!.GetValue<int>());
            Assert.Equal(1, report["candidate"]!["rejected_before_dispatch"]!.GetValue<int>());
            Assert.Equal(1, report["candidate"]!["request_phases"]!["construction_select"]!["rejected_before_dispatch"]!.GetValue<int>());
        }
        if (defect == "unknown_cost")
        {
            Assert.Null(report["candidate"]!["estimated_cost_eur"]!["total"]);
            Assert.Equal(8m, report["candidate"]!["estimated_cost_eur"]!["known_sum"]!.GetValue<decimal>());
        }
        else Assert.Equal((decimal)after.Length, report["candidate"]!["estimated_cost_eur"]!["total"]!.GetValue<decimal>());
    }
}
