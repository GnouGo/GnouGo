using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LiveIntentAgentGenerationTests
{
    private static PlanningSnapshot PairedSeed()
    {
        var behavior = new PlanningBehaviorPlan { Summary = "Return value", Workflows = [new() { Key = "main", Purpose = "Return value",
            Steps = [new() { Key = "value", Purpose = "Return value", InputDependencies = [] }] }] };
        var preparation = new PlanningPreparation { AllowedStepTypes = ["set"] };
        return new()
        {
            Request = new() { TenantId = "paired", SessionId = "parent", Prompt = "Return value", Options = new() { ["generator"] = new JsonObject { ["model"] = "test" } },
                ConstructionStrategy = PlanningConstructionStrategies.TypedWorkflowsV1, Generation = new() { MaxInputTokensPerUnit = 32_000 } },
            Status = PlanningStatus.Generating, IntentChecked = true, BehaviorPlan = behavior, Preparation = preparation,
            ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(behavior), Graph = PlanningBehaviorPlans.Display(behavior, preparation),
            Usage = new() { Calls = 8, InputTokens = 100, OutputTokens = 20, EstimatedCost = 1 }, ActiveMilliseconds = 120_000,
            Answers = [new("question", new() { ["answer"] = "private" })]
        };
    }
    private static PlanningSnapshot PairedFresh(string strategy) => new()
    {
        Request = new() { TenantId = "paired", SessionId = Guid.NewGuid().ToString("N"), Prompt = "Return value",
            Options = new() { ["generator"] = new JsonObject { ["model"] = "test" } },
            ConstructionStrategy = strategy, Generation = new() { MaxInputTokensPerUnit = 32_000 } }
    };

    [Fact]
    public void PairedCohortsPassExistingValidationBeforeAnyPaidPreparation()
    {
        foreach (var strategy in new[] { PlanningConstructionStrategies.TypedWorkflowsV1, PlanningConstructionStrategies.JavaScriptV1 })
            for (var pair = 1; pair <= 3; pair++) ValidateLiveCohort(PairedCohort(strategy, "20260909181242-6cd2b6", pair));
        Assert.Throws<InvalidOperationException>(() => ValidateLiveCohort("typed-workflows-v1-20260909181242-6cd2b6-1"));
    }

    [Fact]
    public void PairedForksHaveIdenticalApprovedInputsAndIndependentState()
    {
        var seed = PairedSeed(); var fresh = PairedFresh(PlanningConstructionStrategies.JavaScriptV1);
        var js = ForkComparisonCheckpoint(fresh, seed);
        var json = ForkComparisonCheckpoint(PairedFresh(PlanningConstructionStrategies.TypedWorkflowsV1), seed);
        Assert.Equal(0, fresh.Revision); Assert.Null(fresh.Preparation);
        Assert.Equal(ComparisonCheckpointFingerprint(seed), ComparisonCheckpointFingerprint(js));
        Assert.Equal(ComparisonCheckpointFingerprint(js), ComparisonCheckpointFingerprint(json));
        Assert.Null(js.Usage); Assert.Equal(0, js.ActiveMilliseconds); Assert.Empty(js.Attempts); Assert.Empty(js.SourceCandidates);
        Assert.Null(js.ApprovedHash); Assert.Null(js.Yaml); Assert.NotEqual(js.Request.SessionId, json.Request.SessionId);
        js.Graph!.Workflows.Clear(); js.Answers[0].Answers["answer"] = "changed";
        Assert.Single(seed.Graph!.Workflows); Assert.Single(json.Graph!.Workflows);
        Assert.Equal("private", json.Answers[0].Answers["answer"]!.ToString());
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("tenant")]
    [InlineData("model")]
    [InlineData("limits")]
    [InlineData("constructed")]
    public void PairedForkRejectsChangedOrUnapprovedInputs(string change)
    {
        var seed = PairedSeed(); var fresh = PairedFresh(PlanningConstructionStrategies.JavaScriptV1);
        switch (change)
        {
            case "approval": seed.ApprovedBehaviorHash = "wrong"; break;
            case "tenant": fresh.Request.TenantId = "other"; break;
            case "model": fresh.Request.Options["generator"]!["model"] = "different"; break;
            case "limits": fresh.Request.Generation.MaxInputTokensPerUnit = 12_000; break;
            case "constructed": seed.SourceCandidates.Add(new() { Calls = 1 }); break;
        }
        Assert.Throws<InvalidOperationException>(() => ForkComparisonCheckpoint(fresh, seed));
    }

    [Fact]
    public void PairedAccountingDeductsPrefixWithoutCopyingItsCharges()
    {
        var seed = PairedSeed(); var arm = new ComparisonAttempt(PlanningConstructionStrategies.JavaScriptV1, 1, "test") { Seed = seed, InputTokenCeiling = 32_000 };
        var fork = ForkComparisonCheckpoint(PairedFresh(PlanningConstructionStrategies.JavaScriptV1), seed);
        fork.Usage = new() { Calls = 2, EstimatedCost = 0.5m }; fork.ActiveMilliseconds = 30_000;
        arm.Capture(fork); arm.BeginUsage(new() { Calls = 10, EstimatedCost = 2 }, 0);
        arm.EndUsage(new() { Calls = 12, EstimatedCost = 2.5m }, 0);
        var report = arm.ToJson();
        Assert.Equal(92, arm.RemainingCalls);
        Assert.Equal(10, report["logicalGenerationCalls"]!.GetValue<long>());
        Assert.Equal(150_000, report["logicalGenerationActiveMilliseconds"]!.GetValue<double>());
        Assert.Equal(2, report["totalAttemptCalls"]!.GetValue<long>());
        Assert.Equal(0.5m, report["totalAttemptEstimatedCost"]!.GetValue<decimal>());
        Assert.Equal(32_000, report["inputTokenCeiling"]!.GetValue<int>());
        Assert.Equal(12_000, new TypedWorkflowPlanningSettings().MaxInputTokensPerUnit);
    }

    [Fact]
    public void PairedEnvironmentRejectsCatalogAndModelDrift()
    {
        var env = new FrozenComparisonEnvironment(); var seed = PairedSeed();
        seed.PreparationCheckpoint = new() { ValidatedResults = new() { ["discovery"] = new JsonArray("catalog") } };
        env.Check(seed); env.Check(seed);
        seed.PreparationCheckpoint.ValidatedResults["discovery"] = new JsonArray("changed");
        Assert.Throws<InvalidOperationException>(() => env.Check(seed));
        seed.PreparationCheckpoint = null; seed.Request.Options["generator"]!["model"] = "changed";
        Assert.Throws<InvalidOperationException>(() => env.Check(seed));
    }

    [Fact]
    public void PairedReportsOmitGeneratorGuidanceAndRetainBlockedPreparationUsage()
    {
        var seed = PairedSeed(); seed.Request.Options["generator"]!["context"] = "PRIVATE_GENERATOR_GUIDANCE";
        var env = new FrozenComparisonEnvironment(); env.Check(seed);
        Assert.DoesNotContain("PRIVATE_GENERATOR_GUIDANCE", env.PublicGenerator.ToJsonString()); Assert.NotNull(env.GeneratorFingerprint);
        var blocked = new ComparisonAttempt(PlanningConstructionStrategies.JavaScriptV1, 1, "blocked")
        { Outcome = "blocked_preparation", SharedPreparation = seed };
        var report = blocked.ToJson();
        Assert.Equal(8, report["logicalGenerationCalls"]!.GetValue<long>());
        Assert.Equal(120_000, report["logicalGenerationActiveMilliseconds"]!.GetValue<double>());
        Assert.Equal(0, report["totalAttemptCalls"]!.GetValue<long>());
        Assert.Equal("shared_preparation", report["failedPhase"]!.ToString());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("calls")]
    [InlineData("background")]
    public void PairedSettingsRejectConfigurationOverlaysThatChangeTheExperiment(string change)
    {
        var attempt = new ComparisonAttempt(PlanningConstructionStrategies.JavaScriptV1, 1, "test") { InputTokenCeiling = 32_000, Seed = PairedSeed() };
        var settings = new TypedWorkflowPlanningSettings { ConstructionStrategy = attempt.Strategy, BackgroundProcessingEnabled = false,
            MaxInputTokensPerUnit = 32_000, MaxModelCalls = attempt.RemainingCalls };
        ValidateComparisonSettings(settings, attempt);
        switch (change)
        {
            case "input": settings.MaxInputTokensPerUnit = 12_000; break;
            case "calls": settings.MaxModelCalls = 100; break;
            case "background": settings.BackgroundProcessingEnabled = true; break;
        }
        Assert.Throws<InvalidOperationException>(() => ValidateComparisonSettings(settings, attempt));
    }
}
