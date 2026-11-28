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
    [InlineData("construction_discovery", false, false, 1)]
    [InlineData("construction_draft", false, false, 1)]
    [InlineData("construction_local_judge", true, false, 1)]
    [InlineData("construction_global_judge", false, true, 0)]
    public void IssuedRequestCapturesRemainingMandatoryReviews(string phase, bool association, bool complete, int expected)
    {
        var state = Fresh();
        if (association)
        {
            state.Construction!.Requirements = [new() { Id = "current" }, new() { Id = "later" }];
            state.Construction.Compact!.CurrentStep = new() { CloseRequirementIds = ["current"] };
        }
        if (association || complete)
            state.Construction!.Candidate = new() { Extension = new() { Complete = complete, CompleteBlock = true,
                ContinuationBinding = association ? new() { ContinuationId = "consumer", Binding = new() } : null } };
        Assert.Equal(expected, Prepare(state, phase).RequiredReviewCalls);
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
    [Fact]
    public void IndependentPolicyIsExplicitImmutableAndRequiresToolFreeSynchronousRequests()
    {
        var state = new PlanningSession();
        PlanningTransportPolicies.Capture(state.Request, new() { Version = 2, MaxUncertainRetries = 1,
            IndependentGenerationMaxElapsedMilliseconds = 1_260_000 });
        PlanningSessionCreation.Capture(state);
        var request = Prepare(state, "construction_intent");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal(1, PlanningTransportPolicies.Read(restored.Request)!.MaxUncertainRetries);
        Assert.Equal(1_260_000, PlanningTransportPolicies.Read(restored.Request)!.IndependentGenerationMaxElapsedMilliseconds);
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.Capture(restored.Request, new()));
        request.UseBackgroundMode = true;
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, restored.Request));
        request.UseBackgroundMode = false; request.Tools = [new() { Name = "external_effect" }];
        Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.ValidateIssued(request, restored.Request));
    }

    [Theory]
    [InlineData(1, 1, 100)] [InlineData(2, 0, 100)] [InlineData(2, 3, 100)]
    [InlineData(2, 1, 0)] [InlineData(2, 1, 3_660_001)]
    public void InvalidIndependentPoliciesDoNotGainAuthority(int version, int uncertain, int elapsed)
        => Assert.Throws<PlanningConflictException>(() => PlanningTransportPolicies.Validate(new() {
            Version = version, MaxUncertainRetries = uncertain, IndependentGenerationMaxElapsedMilliseconds = elapsed }));
    private static JsonObject Schema() => PlanningSchemas.Object(("value", PlanningSchemas.String()));
    private static LLMRequest Prepare(PlanningSession state, string phase) => PlanningModelCalls.PrepareRequest(state, phase, "bounded", Schema());
}
