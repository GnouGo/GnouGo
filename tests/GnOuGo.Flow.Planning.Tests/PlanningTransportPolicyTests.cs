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
    [InlineData("construction_select", false, false, 1)]
    [InlineData("construction_discovery", false, true, 2)]
    [InlineData("construction_draft", true, true, 2)]
    [InlineData("construction_local_judge", true, false, 1)]
    [InlineData("construction_local_judge", false, true, 0)]
    [InlineData("construction_intermediate_judge", true, true, 0)]
    public void IssuedRequestCapturesRemainingMandatoryReviews(string phase, bool intermediate, bool accepted, int expected)
    {
        var state = Fresh();
        if (accepted) state.Construction!.Checkpoints.Add(new());
        if (intermediate || phase.Contains("judge", StringComparison.Ordinal))
            state.Construction!.Candidate = new() { NeedsIntermediateReview = intermediate };
        Assert.Equal(expected, Prepare(state, phase).RequiredReviewCalls);
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
