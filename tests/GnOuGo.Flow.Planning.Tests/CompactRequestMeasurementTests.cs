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
        Assert.Equal(1, PlanningBenchmarkMeasurements.RequestMetrics(run)["phases"]!["construction_select"]!["rejected_before_dispatch"]!.GetValue<int>());
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
    public void CompactComparisonPermitsMoreCallsButRequiresIndependentCompleteBusinessEvidence(string defect, string expected)
    {
        JsonObject[] Arm(bool compact) => new[] { "collections", "amazon", "code" }.SelectMany(name => Enumerable.Range(1, 3).Select(repetition =>
        {
            var request = new PlanningRequest();
            PlanningBenchmarkMeasurements.ConfigureRequest(request, [], false);
            var measurement = PlanningRequestMeasurements.Measure(new LLMRequest { Prompt = new string('a', compact ? 1000 : 5000) }, "construction_draft", compact ? 8000 : 32000);
            measurement.Admission = "admitted";
            return new JsonObject
            {
                ["case"] = name, ["repetition"] = repetition, ["source_commit"] = compact ? "new" : "old", ["mode"] = "live",
                ["comparison_manifest"] = "same-producers-oracles-inputs", ["provider"] = "provider", ["model"] = "model", ["campaign"] = "retained",
                ["configuration"] = PlanningBenchmarkMeasurements.Configuration(request), ["independent_business_success"] = compact,
                ["safety_violations"] = new JsonArray(), ["calls"] = compact ? 20 : 2,
                ["input_tokens"] = compact ? 20_000 : 10_000, ["output_tokens"] = 500, ["estimated_cost_eur"] = 1m, ["elapsed_ms"] = 500,
                ["request_metrics"] = new JsonObject { ["requests"] = new JsonArray(JsonSerializer.SerializeToNode(measurement, PlanningJsonContext.Default.PlanningRequestMeasurement)) }
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
        var report = PlanningBenchmarkMeasurements.CompareCompactConstruction(before, after, ["collections", "amazon", "code"]);
        Assert.Equal(expected, report["status"]!.ToString());
        Assert.Equal(20m, report["candidate"]!["calls"]!["median"]!.GetValue<decimal>());
        Assert.Equal(2m, report["baseline"]!["calls"]!["median"]!.GetValue<decimal>());
        Assert.Equal(after.Length * 20m, report["candidate"]!["calls"]!["total"]!.GetValue<decimal>());
        Assert.Equal(after.Length * 500m, report["candidate"]!["elapsed_ms"]!["total"]!.GetValue<decimal>());
        Assert.Equal(60m, report["cases"]!["collections"]!["candidate"]!["calls"]!["total"]!.GetValue<decimal>());
        Assert.Equal(1500m, report["cases"]!["collections"]!["candidate"]!["elapsed_ms"]!["total"]!.GetValue<decimal>());
        if (defect == "unknown_cost")
        {
            Assert.Null(report["candidate"]!["estimated_cost_eur"]!["total"]);
            Assert.Equal(8m, report["candidate"]!["estimated_cost_eur"]!["known_sum"]!.GetValue<decimal>());
        }
        else Assert.Equal((decimal)after.Length, report["candidate"]!["estimated_cost_eur"]!["total"]!.GetValue<decimal>());
    }
}
