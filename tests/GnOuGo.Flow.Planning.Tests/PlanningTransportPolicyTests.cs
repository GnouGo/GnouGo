using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningTransportPolicyTests
{
    [Fact]
    public void FreshPolicyIsCapturedBeforeRequestAndArtifactIdentityAndDoesNotDrift()
    {
        var state = Fresh();
        var policy = PlanningTransportPolicies.Read(state.Request)!;
        Assert.Equal(3, policy.MaxAttempts); Assert.Equal(0, policy.MaxUncertainRetries);
        var request = Prepare(state, "construction_select");
        Assert.False(request.DisableTransportRetries); Assert.False(request.UseBackgroundMode);
        Assert.Equal(1, request.RequiredReviewCalls);
        PlanningConstructionPhasePolicies.ValidateIssued(request, state.Request);
        var id = request.ClientRequestId; request.ClientRequestId = null;
        Assert.EndsWith(PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)), id!);
        state.Yaml = "retained artifact";
        var artifact = state.ComputeArtifactHash();
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        PlanningSessionCreation.Capture(state);
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.Capture(state.Request, new() { MaxAttempts = 1 }));
        state.Request.Options[PlanningTransportPolicies.OptionName]!["maxAttempts"] = 1;
        Assert.NotEqual(artifact, state.ComputeArtifactHash());
    }

    [Fact]
    public void ExplicitSingleAttemptAndSynchronousAuthorityRemainStricterThanFreshPolicy()
    {
        var state = Fresh();
        var request = PlanningConstructionPhasePolicies.Apply(new() { DisableTransportRetries = true, UseBackgroundMode = true }, state.Request, "construction_select");
        Assert.True(request.DisableTransportRetries); Assert.False(request.UseBackgroundMode);
        request.StructuredOutputSchema = new JsonObject { ["title"] = "construction_select" };
        request.RequiredReviewCalls = 1;
        PlanningConstructionPhasePolicies.ValidateIssued(request, state.Request);
        request.UseBackgroundMode = true;
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionPhasePolicies.ValidateIssued(request, state.Request));
        request.UseBackgroundMode = false; request.RequiredReviewCalls = null;
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionPhasePolicies.ValidateIssued(request, state.Request));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void SourceWindowHeadroomIsBoundedByCapturedCallsWithoutChangingHistoricalRequests(int presentation)
    {
        var state = Fresh();
        state.Request.Options["construction_presentation_version"] = presentation;
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.MaxModelCalls = 7;
        state.Request.Options["llm_budget"] = new JsonObject { ["max_calls"] = 5 };
        var request = Prepare(state, "construction_select");
        var original = JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest);
        foreach (var count in new[] { 0, 1, 2, 3, 5, 6, 8 })
        {
            request.RequiredReviewCalls = count;
            if (count <= (presentation is 6 or 7 or 8 or 9 or 10 or 11 ? 5 : 2)) PlanningTransportPolicies.ValidateIssued(request, state.Request);
            else Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, state.Request));
        }
        request.RequiredReviewCalls = null;
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, state.Request));
        request.RequiredReviewCalls = -1;
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, state.Request));
        Assert.Equal(original, JsonSerializer.Serialize(state.Request, PlanningJsonContext.Default.PlanningRequest));
    }

    [Theory]
    [InlineData("construction_select", false, false, 1, 1)]
    [InlineData("construction_discovery", false, true, 2, 1)]
    [InlineData("construction_draft", true, true, 2, 1)]
    [InlineData("construction_local_judge", true, false, 1, 0)]
    [InlineData("construction_local_judge", false, true, 0, 0)]
    [InlineData("construction_intermediate_judge", true, true, 0, 0)]
    public void IssuedRequestCapturesRemainingMandatoryReviews(string phase, bool intermediate, bool accepted, int historical, int current)
    {
        foreach (var version in new[] { 4, 5 })
        {
            var state = Fresh();
            state.Construction!.Compact!.PresentationVersion = version;
            state.Request.Options["construction_presentation_version"] = version;
            if (accepted) state.Construction.Checkpoints.Add(new());
            if (intermediate || phase.Contains("judge", StringComparison.Ordinal))
                state.Construction.Candidate = new() { NeedsIntermediateReview = intermediate };
            Assert.Equal(version == 4 ? historical : current, Prepare(state, phase).RequiredReviewCalls);
        }
    }

    [Theory]
    [InlineData(3)][InlineData(4)]
    public async Task HistoricalFutureAndPendingRequestsRetainSingleAttemptBackgroundAndExactBytes(int presentation)
    {
        var state = Fresh(); state.Request.Options.Remove(PlanningTransportPolicies.OptionName);
        state.Request.Options["construction_presentation_version"] = presentation;
        state.Construction!.Compact!.PresentationVersion = presentation;
        var request = Prepare(state, "construction_select");
        Assert.True(request.DisableTransportRetries); Assert.True(request.UseBackgroundMode); Assert.Null(request.RequiredReviewCalls);
        Assert.DoesNotContain("requiredReviewCalls", JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "construction_select", Request = request };
        state.ModelCalls = 1;
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        state = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)!;
        PlanningSessionCreation.Capture(state);
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["value"] = "retained" } } };
        await PlanningModelCalls.CallAsync(state, runtime, "construction_select", "new context must not replace pending bytes", Schema(), PlannerFixture.Ct);
        Assert.Equal(exact, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("null")][InlineData("{}")]
    [InlineData("{\"version\":2,\"maxAttempts\":3,\"maxUncertainRetries\":0,\"attemptTimeoutMilliseconds\":100,\"baseDelayMilliseconds\":1,\"maxDelayMilliseconds\":1,\"maxTotalDelayMilliseconds\":1}")]
    public void MalformedPolicyNeverAcquiresDefaults(string json)
    {
        var request = new PlanningRequest(); request.Options[PlanningTransportPolicies.OptionName] = JsonNode.Parse(json);
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.Read(request));
    }

    private static PlanningSession Fresh() { var state = new PlanningSession(); PlanningSessionCreation.Capture(state); return state; }
    private static JsonObject Schema() => PlanningSchemas.Object(("value", PlanningSchemas.String()));
    private static LLMRequest Prepare(PlanningSession state, string phase) => PlanningModelCalls.PrepareRequest(state, phase, "bounded", Schema());
}
