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
    [InlineData("collections", "high", "high", 8192)]
    [InlineData("amazon", "medium", "medium", 16384)]
    [InlineData("review-pr", "medium", "high", 8192)]
    public void EnlargedDiagnosticProfilesAreProspectiveAndDoNotRaiseExistingSessionAllowances(string scenario,
        string draftReasoning, string reviewReasoning, int reviewOutput)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DiagnosticProfiles", "compact-diagnostic-" + scenario + ".json");
        var state = new PlanningSession(); PlanningSessionCreation.Capture(state);
        var original = PlanningBenchmarkMeasurements.Configuration(state.Request).ToJsonString();
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(state.Request,
            ["--phase-policy", file], retained: true));
        Assert.Equal(original, PlanningBenchmarkMeasurements.Configuration(state.Request).ToJsonString());
        var fresh = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(fresh, ["--phase-policy", file, "--max-input-tokens", "32000",
            "--max-output-tokens", "32768"], retained: false);
        var policy = PlanningConstructionPhasePolicies.Read(fresh)!;
        Assert.Equal(new[] { 3000, 8000, 4000 }, new[] { policy.Navigation.TargetInputTokens, policy.Draft.TargetInputTokens, policy.Review.TargetInputTokens });
        Assert.Equal(new[] { 24000, 32000, 24000 }, new[] { policy.Navigation.MaxInputTokens, policy.Draft.MaxInputTokens, policy.Review.MaxInputTokens });
        Assert.Equal("high", policy.Navigation.Reasoning); Assert.Equal(draftReasoning, policy.Draft.Reasoning); Assert.Equal(reviewReasoning, policy.Review.Reasoning);
        Assert.Equal(32768, policy.Navigation.OutputTokens); Assert.Equal(16384, policy.Draft.OutputTokens); Assert.Equal(reviewOutput, policy.Review.OutputTokens);
        Assert.Equal(64, fresh.MaxModelCalls); Assert.Equal(4, fresh.MaxReplanAttempts); Assert.Equal(300, fresh.Policy.MaxStepsTotal);
        var restored = PlanningBenchmarkMeasurements.RestoreConfiguration(PlanningBenchmarkMeasurements.Configuration(fresh));
        Assert.Equal(PlanningBenchmarkMeasurements.PhasePolicyHash(fresh), PlanningBenchmarkMeasurements.PhasePolicyHash(restored));
        fresh.Generation.MaxInputTokensPerRequest = 10000;
        Assert.Equal(10000, PlanningConstructionPhasePolicies.Resolve(fresh, PlanningConstruction.DraftPurpose).MaxInputTokens);
    }

    [Theory]
    [InlineData("construction_intent", 3000, 6000, 8192)]
    [InlineData("construction_select", 3000, 6000, 8192)]
    [InlineData("construction_discovery", 3000, 6000, 8192)]
    [InlineData("construction_draft", 8000, 16000, 8192)]
    [InlineData("replan", 8000, 16000, 8192)]
    [InlineData("construction_local_judge", 4000, 8000, 2048)]
    [InlineData("construction_global_judge", 4000, 8000, 2048)]
    public void FreshCaptureSeparatesPresentationTargetFromCompleteRequestCeiling(string purpose, int target, int maximum, int output)
    {
        var state = new PlanningSession();
        PlanningSessionCreation.Capture(state);
        var settings = PlanningConstructionPhasePolicies.Resolve(state.Request, purpose);
        Assert.NotNull(state.Construction!.Limits.PhasePolicy);
        Assert.Equal(target, settings.TargetInputTokens);
        Assert.Equal(maximum, settings.MaxInputTokens);
        Assert.Equal(output, settings.OutputTokens);
        Assert.Equal(maximum, PlanningConstructionInputPolicy.EffectiveLimit(state.Request, purpose));
        Assert.Equal("medium", settings.Reasoning);
    }


    [Fact]
    public void ExplicitTighterCapturedPhaseInputsRemainHardCeilings()
    {
        var state = new PlanningSession { Construction = new() { Limits = new()
        {
            PhasePolicy = new()
            {
                Navigation = new() { TargetInputTokens = 2_000, MaxInputTokens = 2_000 },
                Draft = new() { TargetInputTokens = 4_000, MaxInputTokens = 4_000 },
                Review = new() { TargetInputTokens = 1_000, MaxInputTokens = 1_000 }
            }
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
        var request = new PlanningRequest { Options = new() { ["construction_limits"] = new JsonObject { ["phasePolicy"] = JsonNode.Parse(value) } } };
        Assert.Throws<PlanningConflictException>(() => PlanningConstructionPhasePolicies.Resolve(request, "construction_select"));
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
        Assert.True(draft.RequireOutputTokenLimit); Assert.False(draft.DisableTransportRetries); Assert.False(draft.UseBackgroundMode);
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
        Assert.Equal(3000, admitted.InputTarget);
        Assert.NotNull(admitted.TargetWarning);
        Assert.Contains("maximum 6000", admitted.TargetWarning, StringComparison.Ordinal);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime, "construction_select",
            new string('x', 20000), schema, PlannerFixture.Ct, 8192));
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("rejected_before_dispatch", state.RequestMeasurements!.Last().Admission);
        Assert.Null(state.RequestMeasurements!.Last().TargetWarning);
    }

    [Theory]
    [InlineData("construction_discovery")]
    [InlineData("replan")]
    [InlineData("construction_local_judge")]
    [InlineData("construction_global_judge")]
    public void EvidenceAdmissionRecordsOneAdvisoryPerExactRequestWithoutRepairOrApprovalChanges(string purpose)
    {
        var state = new PlanningSession { Yaml = "unchanged artifact" };
        PlanningSessionCreation.Capture(state);
        var phaseName = purpose == "replan" ? "draft" : purpose.EndsWith("judge", StringComparison.Ordinal) ? "review" : "navigation";
        var phase = state.Request.Options["construction_limits"]!["phasePolicy"]![phaseName]!;
        phase["targetInputTokens"] = 1000; phase["maxInputTokens"] = 3000;
        var schema = PlanningSchemas.Object(("value", PlanningSchemas.String()));
        var shortRequest = PlanningModelCalls.PrepareRequest(state, purpose, "Bounded evidence.", schema);
        PlanningConstruction.AdmitEvidenceFollowup(state, shortRequest, purpose);
        Assert.Null(Assert.Single(state.RequestMeasurements!).TargetWarning);
        var larger = PlanningModelCalls.PrepareRequest(state, purpose, new string('x', 4000), schema);
        var beforeHash = state.ComputeArtifactHash();
        PlanningConstruction.AdmitEvidenceFollowup(state, larger, purpose);
        var warning = state.RequestMeasurements!.Last();
        Assert.InRange(warning.EstimatedInputTokens, 1001, 3000);
        Assert.NotNull(warning.TargetWarning);
        Assert.Contains("response_schema=", warning.TargetWarning, StringComparison.Ordinal);
        Assert.Contains("instructions=", warning.TargetWarning, StringComparison.Ordinal);
        PlanningConstruction.AdmitEvidenceFollowup(state, larger, purpose);
        Assert.Equal(2, state.RequestMeasurements!.Count);
        Assert.Empty(state.Diagnostics); Assert.Empty(state.Construction!.Candidate?.Diagnostics ?? []);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(beforeHash, state.ComputeArtifactHash());
        var restored = PlannerFixture.Clone(state);
        Assert.Equal(warning.TargetWarning, restored.RequestMeasurements!.Last().TargetWarning);
        PlanningConstruction.AdmitEvidenceFollowup(restored, larger, purpose);
        Assert.Equal(2, restored.RequestMeasurements!.Count);
        var report = PlanningBenchmarkMeasurements.RequestMetrics(new JsonObject
            { ["session"] = JsonSerializer.SerializeToNode(restored, PlanningJsonContext.Default.PlanningSession) })["phases"]![purpose]!;
        Assert.Equal(1, report["above_target_warnings"]!.GetValue<int>());
        Assert.Equal(warning.CompleteRequestBytes, report["p95_complete_request_bytes"]!.GetValue<long>());
        var oversized = PlanningModelCalls.PrepareRequest(state, purpose, new string('x', 12000), schema);
        Assert.Throws<PlanningResponseException>(() => PlanningConstruction.AdmitEvidenceFollowup(state, oversized, purpose));
        Assert.Equal("rejected_before_dispatch", state.RequestMeasurements!.Last().Admission);
        Assert.Null(state.RequestMeasurements!.Last().TargetWarning);
        Assert.Empty(state.Diagnostics); Assert.Equal(beforeHash, state.ComputeArtifactHash());
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
