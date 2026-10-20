using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactReviewMeasurementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshIntermediateAdmissionCapturesHostBaselineWithoutChangingRequest(bool reject)
    {
        var state = State();
        var runtime = new TestRuntime { Respond = (_, _) => Response() };
        var request = Request(reject);
        if (reject)
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Call(state, runtime, request));
            Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
            Assert.Empty(runtime.Calls);
            Assert.Equal(0, state.ModelCalls);
        }
        else await Call(state, runtime, request);
        var measurement = Assert.Single(state.RequestMeasurements!);
        Assert.Equal(state.Construction!.Candidate!.BaseHash, measurement.BaselineHash);
        Assert.Equal(reject ? "rejected_before_dispatch" : "admitted", measurement.Admission);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
        if (!reject)
        {
            var issued = Assert.Single(runtime.Calls);
            var direct = PlanningRequestMeasurements.Measure(issued, PlanningConstruction.IntermediatePurpose, 4000);
            Assert.Null(direct.BaselineHash);
            Assert.Equal(direct.RequestHash, measurement.RequestHash);
            Assert.Equal(direct.SchemaHash, measurement.SchemaHash);
            Assert.Equal(direct.CompleteRequestBytes, measurement.CompleteRequestBytes);
        }
    }

    [Fact]
    public async Task PendingIntermediateRecoveryKeepsOriginalMeasurementAndExactIssuedRequest()
    {
        var state = State();
        var request = Request(false);
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(state, runtime, request));
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Single());
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var measurement = JsonSerializer.Serialize(saved.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement);
        var calls = saved.ModelCalls;
        // Issued request metadata, rather than newly supplied presentation, governs continuation.
        saved.Construction!.Candidate!.BaseHash = new string('b', 64);
        var recovery = new TestRuntime { Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Response();
        } };
        request.Prompt = "A newer presentation must not replace the issued request.\n{}";
        await Call(saved, recovery, request);
        Assert.Equal(calls, saved.ModelCalls);
        Assert.Null(saved.PendingCall);
        Assert.Single(recovery.Calls);
        Assert.Equal(measurement, JsonSerializer.Serialize(saved.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement));
    }

    private static PlanningSession State() => new()
    {
        Request = new() { TenantId = "review-metrics", SessionId = "review-metrics",
            Options = new() { ["construction_version"] = 2, ["construction_presentation_version"] = 2 } },
        Construction = new() { Version = 2, Compact = new() { PresentationVersion = 2 },
            Candidate = new() { BaseHash = new string('a', 64) } }
    };
    private static LLMRequest Request(bool oversized) => PlanningCompactConstruction.Request("Review the original evidence.",
        new JsonObject { ["evidence"] = oversized ? new string('x', 15000) : "Exact original evidence." },
        HybridWorkflowPlanner.ConstructionReviewSchema(compact: true), PlanningConstruction.IntermediatePurpose);
    private static Task<JsonNode> Call(PlanningSession state, TestRuntime runtime, LLMRequest request) =>
        PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.IntermediatePurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 2048);
    private static LLMResponse Response() => new() { Json = new JsonObject
    {
        ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null
    } };
}
