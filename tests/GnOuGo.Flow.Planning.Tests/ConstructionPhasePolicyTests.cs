using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionPhasePolicyTests
{
    [Theory]
    [InlineData("construction_intent", 3000, 6000, 8192)]
    [InlineData("construction_select", 3000, 6000, 8192)]
    [InlineData("construction_discovery", 3000, 6000, 8192)]
    [InlineData("construction_draft", 8000, 16000, 8192)]
    [InlineData("replan", 8000, 16000, 8192)]
    [InlineData("construction_local_judge", 4000, 8000, 2048)]
    [InlineData("construction_intermediate_judge", 4000, 8000, 2048)]
    public void FreshCaptureSeparatesPresentationTargetFromCompleteRequestCeiling(string purpose, int target, int maximum, int output)
    {
        var state = new PlanningSession();
        PlanningSessionCreation.Capture(state);
        var settings = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose);
        Assert.True(PlanningConstructionPhasePolicies.IsFresh(state.Request));
        Assert.Equal(target, settings.TargetInputTokens);
        Assert.Equal(maximum, settings.MaxInputTokens);
        Assert.Equal(output, settings.OutputTokens);
        Assert.Equal(maximum, PlanningConstructionInputPolicy.EffectiveLimit(state.Request, purpose));
        Assert.Equal("medium", settings.Reasoning);
    }

    [Fact]
    public void HistoricalAbsenceKeepsLimitsSerializationAndIssuedRequest()
    {
        var state = JsonSerializer.Deserialize("""{"request":{"options":{"construction_version":2}}}""",
            PlanningJsonContext.Default.PlanningSession)!;
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        PlanningSessionCreation.Capture(state);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.False(PlanningConstructionPhasePolicies.IsFresh(state.Request));
        Assert.Equal(3000, PlanningConstructionInputPolicy.EffectiveLimit(state.Request, "construction_select"));
        Assert.DoesNotContain("phasePolicy", JsonSerializer.Serialize(new PlanningConstructionOptions(), PlanningJsonContext.Default.PlanningConstructionOptions));
        var request = new LLMRequest { MaxTokens = 123, Prompt = "retained" };
        PlanningConstructionPhasePolicies.Apply(request, state.Request, "construction_select");
        Assert.Equal(123, request.MaxTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitTighterHostInputsRemainHardCeilings(bool explicitProfile)
    {
        var state = new PlanningSession { Construction = new() { Limits = new()
        {
            InputLimits = new() { Selection = 2_000, Draft = 4_000, Review = 1_000 },
            PhasePolicy = explicitProfile ? new() : null
        } } };
        PlanningSessionCreation.Capture(state);
        foreach (var (purpose, maximum) in new[] { ("construction_select", 2_000), ("construction_draft", 4_000), ("construction_local_judge", 1_000) })
        {
            var phase = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose);
            Assert.Equal(maximum, phase.MaxInputTokens);
            Assert.Equal(maximum, phase.TargetInputTokens);
            Assert.Equal(maximum, PlanningConstructionInputPolicy.EffectiveLimit(state.Request, purpose));
        }
    }

    [Fact]
    public void EffectiveCapturedProfileDoesNotDriftBetweenBenchmarkCreationAndPlannerCapture()
    {
        var state = new PlanningSession();
        state.Request.Generation.MaxInputTokensPerRequest = 5000;
        state.Request.Generation.MaxOutputTokens = 1024;
        PlanningBenchmarkMeasurements.ConfigureRequest(state.Request, [], false);
        PlanningBenchmarkMeasurements.CaptureConstructionSettings(state);
        var before = PlanningBenchmarkMeasurements.Configuration(state.Request).ToJsonString();
        PlanningSessionCreation.Capture(state);
        Assert.Equal(before, PlanningBenchmarkMeasurements.Configuration(state.Request).ToJsonString());
        var resolved = PlanningConstructionPhasePolicies.Resolve(state.Request, "construction_draft");
        Assert.Equal(5000, resolved.TargetInputTokens);
        Assert.Equal(5000, resolved.MaxInputTokens);
        Assert.Equal(1024, resolved.OutputTokens);
        var retained = PlannerFixture.Clone(state);
        PlanningSessionCreation.Capture(retained);
        Assert.Equal(before, PlanningBenchmarkMeasurements.Configuration(retained.Request).ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":1,"navigation":{},"draft":{},"review":{}}""")]
    public void PresentMalformedOrUnsupportedPolicyNeverFallsBack(string value)
    {
        var request = new PlanningRequest { Options = new() { ["construction_version"] = 2,
            ["construction_limits"] = new JsonObject { ["phasePolicy"] = JsonNode.Parse(value) } } };
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionPhasePolicies.IsFresh(request));
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionInputPolicy.EffectiveLimit(request, "construction_select"));
    }

    [Fact]
    public void PhaseOutputAndReasoningAreResolvedBeforeRequestIdentityWithoutMutatingOtherFamilies()
    {
        var state = new PlanningSession { Construction = new() { Limits = new() { PhasePolicy = new()
        {
            Navigation = new() { TargetInputTokens = 3000, MaxInputTokens = 6000, Reasoning = "low", OutputTokens = 4096 },
            Draft = new() { TargetInputTokens = 8000, MaxInputTokens = 16000, Reasoning = "high", OutputTokens = 16384 }
        } } } };
        PlanningSessionCreation.Capture(state);
        var original = state.Request.Options.ToJsonString();
        var navigation = PlanningConstructionPhasePolicies.Apply(new() { MaxTokens = 8192 }, state.Request, "construction_select");
        var draft = PlanningConstructionPhasePolicies.Apply(new() { MaxTokens = 8192 }, state.Request, "construction_draft");
        Assert.Equal("low", navigation.Reasoning); Assert.Equal(4096, navigation.MaxTokens);
        Assert.Equal("high", draft.Reasoning); Assert.Equal(16384, draft.MaxTokens);
        Assert.True(draft.RequireOutputTokenLimit); Assert.True(draft.DisableTransportRetries);
        Assert.Equal(2048, PlanningConstructionPhasePolicies.Resolve(state.Request, "construction_local_judge").OutputTokens);
        Assert.Equal(original, state.Request.Options.ToJsonString());
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionPhasePolicies.Resolve(state.Request, "unknown"));
    }

    [Fact]
    public async Task ExceedingTargetDoesNotDispatchAnExtraCallButExceedingMaximumStopsBeforeDispatch()
    {
        var state = new PlanningSession(); PlanningSessionCreation.Capture(state);
        var runtime = new TestRuntime { Respond = (_, _) => new LLMResponse { Json = new JsonObject { ["value"] = "ok" } } };
        var schema = PlanningSchemas.Object(("value", PlanningSchemas.String()));
        await PlanningModelCalls.CallAsync(state, runtime, "construction_select", new string('x', 10000), schema, PlannerFixture.Ct, 8192);
        var admitted = Assert.Single(state.RequestMeasurements!);
        Assert.InRange(admitted.EstimatedInputTokens, 3001, 6000);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime, "construction_select",
            new string('x', 20000), schema, PlannerFixture.Ct, 8192));
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("rejected_before_dispatch", state.RequestMeasurements!.Last().Admission);
    }

    [Fact]
    public void NamedCohortsPreserveOldKeysAndFreezePhaseProfilesSeparately()
    {
        Assert.Null(PlanningBenchmarkMeasurements.Cohort([]));
        Assert.Equal("source:fixture:collections:1", PlanningBenchmarkMeasurements.CorpusRunKey("source", "fixture", null, null, "collections", 1));
        Assert.Equal("source:fixture:judge:collections:1", PlanningBenchmarkMeasurements.CorpusRunKey("source", "fixture", "judge", null, "collections", 1));
        Assert.NotEqual(PlanningBenchmarkMeasurements.CorpusRunKey("source", "fixture", null, "control", "collections", 1),
            PlanningBenchmarkMeasurements.CorpusRunKey("source", "fixture", null, "low", "collections", 1));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.Cohort(["--cohort", "invalid:identity"]));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var profile = new PlanningConstructionPhasePolicy(); profile.Navigation.Reasoning = "low";
            File.WriteAllText(path, JsonSerializer.Serialize(profile, PlanningJsonContext.Default.PlanningConstructionPhasePolicy));
            var request = new PlanningRequest();
            PlanningBenchmarkMeasurements.ConfigureRequest(request, ["--phase-policy", path], false);
            var hash = PlanningBenchmarkMeasurements.PhasePolicyHash(request);
            PlanningBenchmarkMeasurements.ConfigureRequest(request, ["--phase-policy", path], true);
            var restored = PlanningBenchmarkMeasurements.RestoreConfiguration(PlanningBenchmarkMeasurements.Configuration(request));
            Assert.Equal(hash, PlanningBenchmarkMeasurements.PhasePolicyHash(restored));
            profile.Navigation.OutputTokens = 4096;
            File.WriteAllText(path, JsonSerializer.Serialize(profile, PlanningJsonContext.Default.PlanningConstructionPhasePolicy));
            Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(request, ["--phase-policy", path], true));
            Assert.Equal(hash, PlanningBenchmarkMeasurements.PhasePolicyHash(request));
        }
        finally { File.Delete(path); }
    }
}
