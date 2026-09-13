using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ProgressiveCampaignTests
{
    [Fact]
    public void ExactBehaviorAcceptanceWaitsForExplicitContinuationOfTheSameSession()
    {
        Assert.False(ProgressiveRules.ShouldAdvance("accept")); Assert.False(ProgressiveRules.ShouldAdvance("approve"));
        Assert.True(ProgressiveRules.ShouldAdvance("start")); Assert.True(ProgressiveRules.ShouldAdvance("advance"));
        var behavior = new PlanningBehaviorPlan();
        var hash = GnOuGo.Flow.Planning.PlanningBehaviorPlans.Fingerprint(behavior);
        var state = new PlanningSnapshot { Status = PlanningStatus.Generating, BehaviorPlan = behavior, ApprovedBehaviorHash = hash, Graph = new() };
        var stage = new JsonObject { ["status"] = "waiting" };
        ProgressiveRules.RecordBehaviorCheckpoint(stage, state, hash, 0);
        Assert.Equal("behavior_accepted", stage["status"]!.ToString()); Assert.Equal(hash, stage["acceptedBehaviorHash"]!.ToString());
        Assert.NotNull(stage["skeletonHash"]); Assert.Null(state.Outcome); Assert.False(ProgressiveRules.CanPass(1, state, true));
        ProgressiveRules.RequireOpen(JsonNode.Parse(stage.ToJsonString())!.AsObject());
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(new JsonArray(stage.DeepClone()), 1, maximumStage: 1));
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("accepted_behavior")]
    [InlineData("passed")]
    public void CompletedOrArchivedCampaignsCannotResume(string status)
        => Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireOpen(new() { ["status"] = status }));

    [Fact]
    public void RecoveryRecordsAcceptanceOnceAndDoesNotResetAnAdvancedSession()
    {
        var behavior = new PlanningBehaviorPlan(); var hash = GnOuGo.Flow.Planning.PlanningBehaviorPlans.Fingerprint(behavior);
        var state = new PlanningSnapshot { Status = PlanningStatus.Generating, BehaviorPlan = behavior, ApprovedBehaviorHash = hash, Graph = new() };
        var stage = new JsonObject { ["status"] = "waiting" };
        Assert.True(ProgressiveRules.RecoverBehaviorCheckpoint(stage, state));
        var persisted = JsonNode.Parse(stage.ToJsonString())!.AsObject();
        state.CurrentPhase = PlanningPhase.Construction;
        state.RequestAccounting.Add(new() { Id = "durable_construction_request", Evidence = "receipt" });
        Assert.False(ProgressiveRules.RecoverBehaviorCheckpoint(persisted, state));
        Assert.Equal(stage.ToJsonString(), persisted.ToJsonString()); Assert.Single(state.RequestAccounting);
        state.ApprovedBehaviorHash = "changed";
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RecoverBehaviorCheckpoint(persisted, state));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("skeleton")]
    [InlineData("technical")]
    [InlineData("request")]
    [InlineData("construction")]
    public void BehaviorCheckpointRejectsWrongReviewOrAnyExecutableDispatch(string defect)
    {
        var behavior = new PlanningBehaviorPlan(); var hash = GnOuGo.Flow.Planning.PlanningBehaviorPlans.Fingerprint(behavior);
        var state = new PlanningSnapshot { Status = PlanningStatus.Generating, BehaviorPlan = behavior, ApprovedBehaviorHash = hash, Graph = new() };
        if (defect == "hash") state.ApprovedBehaviorHash = "stale";
        if (defect == "skeleton") state.Graph = null;
        if (defect == "technical") state.TechnicalStop = new("ERROR", "behavior", "/field");
        if (defect == "request") state.RequestAccounting.Add(new() { Id = "unexpected" });
        if (defect == "construction") state.Construction.Holes.Add(new() { ExposedRequests = ["unexpected"] });
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RecordBehaviorCheckpoint(new(), state, hash, 0));
    }

    [Fact]
    public void ArchivedPreflightFailureCannotBeRestartedOrBypassed()
    {
        ProgressiveRules.RequirePreflight(null);
        ProgressiveRules.RequirePreflight(new());
        var manifest = new JsonObject { ["preflightStop"] = new JsonObject { ["code"] = "MODEL_CAPABILITY_DISCOVERY_FAILED" } };
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequirePreflight(JsonNode.Parse(manifest.ToJsonString())!.AsObject()));
    }

    [Fact]
    public async Task BenchmarkHydratesPersistedMetadataAndDefaultsBeforeCapabilityChecks()
    {
        var path = AgentMcpTestPersistence.CreateIsolatedDatabasePath("benchmark-metadata");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var configured = FlowLlmCapabilityResolverTests.ConfiguredOptions();
            await AgentMcpTestPersistence.SeedUserConfigAsync(path, new("deployment", "reviewed", null, ModelOverrides: configured.ModelOverrides), ct);
            configured.ModelOverrides.Clear(); configured.DefaultModel = "unreviewed";
            for (var restart = 0; restart < 2; restart++)
            {
                var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions());
                var services = new ServiceCollection().AddSingleton(store).AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>();
                services.AddAgentMcpPersistence(path);
                await using var provider = services.BuildServiceProvider();
                await using var scope = provider.CreateAsyncScope();
                await ProgressiveRules.HydrateModelAsync(store, new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(configured), scope.ServiceProvider.GetRequiredService<IUserConfigRepository>(), ct);
                Assert.Equal("reviewed", store.Current.DefaultModel);
                var resolver = provider.GetRequiredService<ILLMCapabilityResolver>();
                Assert.Equal(["low", "medium"], await resolver.SupportedReasoningLevelsAsync(null, "", ct));
                Assert.True(await resolver.SupportsStructuredOutputAsync(null, "", ct));
            }
        }
        finally { AgentMcpTestPersistence.CleanupIsolatedWorkspace(path); }
    }

    [Fact]
    public void StagesCannotSkipPrerequisitesOrObtainAnotherStartAfterRestart()
    {
        var stages = new JsonArray(new JsonObject { ["status"] = "not_run" }, new JsonObject { ["status"] = "not_run" }, new JsonObject { ["status"] = "not_run" });
        ProgressiveRules.RequireStart(stages, 1);
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(stages, 2));
        stages[0]!["status"] = "starting";
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(JsonNode.Parse(stages.ToJsonString())!.AsArray(), 1));
        stages[0]!["status"] = "passed"; ProgressiveRules.RequireStart(stages, 2);
        stages[1]!["status"] = "passed"; stages[1]!["outcome"] = "need_user_clarification";
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(stages, 3));
        stages[1]!["outcome"] = "valid_workflow"; ProgressiveRules.RequireStart(stages, 3);
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(stages, 4));
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(stages, 2, maximumStage: 1));
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStart(JsonNode.Parse(stages.ToJsonString())!.AsArray(), 3, maximumStage: 1));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void OnlyStageOneAcceptsJustifiedNonWorkflowOutcomes(int stage, bool expected)
    {
        var state = new PlanningSnapshot { Status = PlanningStatus.Clarification, Outcome = new PlanningNeedUserClarification(new("question", ["evidence"], new(), ["obligation"])) };
        Assert.False(ProgressiveRules.CanPass(stage, state, false));
        Assert.Equal(expected, ProgressiveRules.CanPass(stage, state, true));
        state.Status = PlanningStatus.Unsupported; state.Outcome = new PlanningUnsupported([new("obligation", "PROVEN_LIMITATION", ["evidence"])]);
        Assert.Equal(expected, ProgressiveRules.CanPass(stage, state, true));
        state.Outcome = new PlanningUnsupported([]); Assert.False(ProgressiveRules.CanPass(stage, state, true));
        state.Status = PlanningStatus.FinalReview; state.Outcome = null; Assert.False(ProgressiveRules.CanPass(stage, state, true));
        state.Status = PlanningStatus.Approved; state.ArtifactHash = state.ApprovedHash = "exact"; state.Outcome = new PlanningValidWorkflow("exact");
        Assert.True(ProgressiveRules.CanPass(stage, state, false));
        state.TechnicalStop = new("TECHNICAL", "construction", "/field"); Assert.False(ProgressiveRules.CanPass(stage, state, true));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("failed")]
    [InlineData("fixture")]
    [InlineData("catalog")]
    public void ApprovalRequiresEveryExactIndependentFixture(string defect)
    {
        var state = new PlanningSnapshot { Revision = 9, ArtifactHash = "artifact", Status = PlanningStatus.FinalReview };
        var results = new JsonArray(new[] { "a", "b" }.Select(c => (JsonNode)new JsonObject { ["case"] = c, ["artifactHash"] = "artifact", ["fixtureHash"] = "fixture", ["catalogHash"] = "catalog", ["passed"] = true }).ToArray());
        ProgressiveRules.RequireApproval(state, 9, "artifact", results, "fixture", "catalog", ["a", "b"]);
        if (defect == "revision") state.Revision++;
        if (defect == "hash") state.ArtifactHash = "different";
        if (defect == "missing") results.RemoveAt(1);
        if (defect == "duplicate") results[1]!["case"] = "a";
        if (defect == "failed") results[0]!["passed"] = false;
        if (defect == "fixture") results[0]!["fixtureHash"] = "old";
        if (defect == "catalog") results[0]!["catalogHash"] = "old";
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireApproval(state, 9, "artifact", results, "fixture", "catalog", ["a", "b"]));
    }

    [Fact]
    public void BaselineProfileIsLowWithoutChangingProductionDefaultsOrBudgets()
    {
        var settings = ProgressiveRules.Settings();
        Assert.False(settings.BackgroundProcessingEnabled);
        Assert.Equal("low", settings.ReasoningProfile.Routine); Assert.Equal("low", settings.ReasoningProfile.Behavior); Assert.Equal("low", settings.ReasoningProfile.SemanticReview);
        Assert.Equal("medium", new TypedWorkflowPlanningSettings().ReasoningProfile.Behavior);
        Assert.Equal(12000, settings.MaxInputTokensPerRequest); Assert.Equal(8192, settings.MaxOutputTokens); Assert.Equal(4, settings.MaxConcurrency); Assert.Equal(5, settings.MaxRepairsPerWorkflowGate);
    }

    [Fact]
    public void DiagnosticChangesOnlyReasoningAndOwnedIdentityAndCannotBeRepeated()
    {
        var (state, page, request, response) = Diagnostic();
        var before = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var medium = ProgressiveRules.DiagnosticRequest(state, page, request, response, false);
        Assert.Equal("low", request.Reasoning); Assert.Equal("medium", medium.Reasoning); Assert.NotEqual(request.ClientRequestId, medium.ClientRequestId);
        var after = JsonSerializer.SerializeToNode(medium, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        foreach (var name in new[] { "reasoning", "clientRequestId" }) { before.Remove(name); after.Remove(name); }
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.DiagnosticRequest(state, page, request, response, true));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("unverifiable")]
    [InlineData("truncation")]
    [InlineData("sizing")]
    [InlineData("multiple")]
    [InlineData("contract")]
    public void TechnicalAndUnboundedFailuresCannotDispatchADiagnostic(string defect)
    {
        var (state, page, request, response) = Diagnostic();
        if (defect == "provider") state.TechnicalStop = new("LLM_PROVIDER_ERROR", "behavior", "/field");
        if (defect == "unverifiable") state.TechnicalStop = new("REPAIR_REGRESSION", "behavior", "/field", true);
        if (defect == "truncation") response.CompletionStatus = "output_limit";
        if (defect == "sizing") page.EstimatedInputTokens = 2401;
        if (defect == "multiple") page.Decisions.Add("second");
        if (defect == "contract") state.TechnicalStop = new("SCHEMA_REFERENCE_INVALID", "behavior", "/field");
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.DiagnosticRequest(state, page, request, response, false));
    }

    [Fact]
    public void ReportsDeduplicateReplayAndRedactContentWithoutInventingUsage()
    {
        var state = new PlanningSnapshot(); state.Request.Prompt = "PRIVATE_INTENT"; state.Yaml = "PRIVATE_YAML";
        var accounting = new PlanningRequestAccounting { Id = "request", Evidence = "receipt", Phase = "intent", Reasoning = "low", EstimatedInputTokens = 300 };
        state.RequestAccounting = [accounting, accounting];
        var page = new PlanningDecisionPage { Id = "page", Phase = "intent", RequestId = "request", Decisions = ["decision"], Candidate = new() { ["private"] = "PRIVATE_RESPONSE" } };
        state.DecisionPages = [page, page];
        state.Construction.Holes = [new() { Id = "hole", Resolved = true, ResolutionOrigin = "deterministic" }, new() { Id = "container", Superseded = true, Resolved = true, ResolutionOrigin = "deterministic" }];
        state.Diagnostics = [new("ERROR", "/field", "PRIVATE_DIAGNOSTIC")];
        var receipts = new Dictionary<string, LLMResponse?> { ["request"] = new() };
        var report = ProgressiveReport.Build(state, receipts);
        Assert.Equal(1, report["modelCalls"]!.GetValue<int>()); Assert.Equal(1, report["decisionPages"]!.GetValue<int>()); Assert.Equal(1, report["modelDecisions"]!.GetValue<int>());
        Assert.Equal(1, report["engineResolvedExecutableDecisions"]!.GetValue<int>()); Assert.Null(report["otherEngineDecisions"]); Assert.Null(report["inputTokens"]);
        Assert.DoesNotContain("PRIVATE_", report.ToJsonString());
        Assert.True(JsonNode.DeepEquals(report, ProgressiveReport.Build(JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!, receipts)));
    }

    [Fact]
    public void PartitionReportsKeepSemanticChargesAndUnknownHistorySeparate()
    {
        var state = new PlanningSnapshot
        {
            DecisionPages =
            [
                new() { Id = "historical", Correction = true, ParentId = "missing", RequestId = "old" },
                new() { Id = "root", Origin = PlanningDecisionPageOrigin.SemanticCorrection, Correction = true, Gate = PlanningGates.Typed, PartitionChildren = ["child"] },
                new() { Id = "child", ParentId = "root", Origin = PlanningDecisionPageOrigin.OutputPartition, Correction = true, Gate = PlanningGates.Typed, PartitionChildren = ["singleton"] },
                new() { Id = "singleton", ParentId = "child", Origin = PlanningDecisionPageOrigin.OutputPartition, Correction = true, Gate = PlanningGates.Typed,
                    Decisions = ["decision"], Diagnostics = [new("DECISION_OUTPUT_LIMIT", "/decisions/decision", "PRIVATE_MESSAGE")] }
            ],
            RequestAccounting = [new() { Id = "old", Repair = true }]
        };
        var report = ProgressiveReport.Build(state, new Dictionary<string, LLMResponse?>());
        Assert.Equal(2, report["outputPartitions"]!.GetValue<int>()); Assert.Equal(1, report["semanticCorrectionPages"]!.GetValue<int>());
        var lineage = report["pageLineage"]!.AsArray();
        Assert.Equal("Unknown", lineage[0]!["origin"]!.ToString()); Assert.Null(lineage[0]!["partitionDepth"]);
        Assert.Equal(2, lineage[3]!["partitionDepth"]!.GetValue<int>()); Assert.True(lineage[3]!["singletonOutputLimit"]!.GetValue<bool>());
        Assert.Equal(1, report["requestsByPhase"]![0]!["repairReservations"]!.GetValue<int>()); // Never refund old charges.
        Assert.DoesNotContain("PRIVATE_", report.ToJsonString());
    }

    private static (PlanningSnapshot, PlanningDecisionPage, LLMRequest, LLMResponse) Diagnostic()
    {
        var state = new PlanningSnapshot { TechnicalStop = new("REPAIR_REGRESSION", "behavior", "/field") };
        var page = new PlanningDecisionPage { Id = "page", Phase = "behavior", Decisions = ["choice"], EstimatedInputTokens = 400, EstimatedAnswerTokens = 50 };
        var request = new LLMRequest { ClientRequestId = state.Request.SessionId + ":low", Reasoning = "low", Model = "configured", Prompt = "Frozen semantic context", MaxTokens = 8192,
            StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"choice":{"type":"string","enum":["a","b"]}},"required":["choice"],"additionalProperties":false}""") };
        page.RequestId = request.ClientRequestId;
        state.RequestAccounting.Add(new() { Id = request.ClientRequestId, Evidence = "receipt" });
        return (state, page, request, new() { Json = new JsonObject { ["choice"] = "a" } });
    }
}
