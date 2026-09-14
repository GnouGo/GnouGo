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
    // Synthetic canonical fixture; no historical receipt is replaced by these assignments.
    private static PlanningSnapshot ReviewedClassifier()
    {
        var state = new PlanningSnapshot { Revision = 10, DeclarationFingerprint = "proof", BehaviorPlan = new() { Workflows = [new()] } };
        state.Request.Prompt = "Required input record. Optional input threshold is a non-nullable number defaulting to 100 when omitted. Return classifiedResult. category has exactly the values rejected, high, standard. Preserve the original id and amount.";
        string Reference(string text)
        {
            var id = "ref_" + state.References.Count;
            state.References.Add(new(id, state.Request.TenantId + ":" + state.Request.SessionId, 1, "request",
                GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(state.Request.Prompt), "evidence", state.Request.Prompt.IndexOf(text, StringComparison.Ordinal), text.Length));
            return id;
        }
        foreach (var name in new[] { "record", "threshold", "classifiedResult" })
        {
            var direction = name == "classifiedResult" ? "output" : "input";
            var required = name != "threshold";
            var clauses = name == "threshold" ? new[] { "Optional input threshold is a non-nullable number defaulting to 100 when omitted." }
                : name == "classifiedResult" ? new[] { "Return classifiedResult.", "category has exactly the values rejected, high, standard." } : ["Required input record."];
            state.Declarations.Add(new(name, direction, "main", Reference(name), null, required, name == "threshold" ? Reference("100") : null,
                ["candidate_" + name], [], [], clauses.Select(Reference).ToList(), "root_proof"));
            (direction == "input" ? state.BehaviorPlan.Workflows[0].Inputs : state.BehaviorPlan.Workflows[0].Outputs).Add(new(name, "Reviewed evidence", required) { DeclarationId = name });
        }
        return state;
    }

    [Fact]
    public void FrozenClassifierDeclarationsAreReviewedWithoutChangingState()
    {
        var state = ReviewedClassifier();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        ProgressiveRules.RequireStageOneDeclarations(state);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot));
        ProgressiveRules.RequireStageOneDeclarations(JsonSerializer.Deserialize(before, PlanningJsonContext.Default.PlanningSnapshot)!);
    }

    [Theory]
    [InlineData("extra_input")]
    [InlineData("extra_output")]
    [InlineData("default")]
    [InlineData("required")]
    [InlineData("category")]
    [InlineData("foreign")]
    [InlineData("identity")]
    public void DeclarationReviewBlocksIncorrectCanonicalBehavior(string defect)
    {
        var state = ReviewedClassifier();
        if (defect == "extra_input") state.BehaviorPlan!.Workflows[0].Inputs.Add(new("category", "Member", true));
        if (defect == "extra_output") state.BehaviorPlan!.Workflows[0].Outputs.Add(new("description", "Description", true));
        if (defect == "default") state.Declarations[1] = state.Declarations[1] with { DefaultReference = null };
        if (defect == "required") state.Declarations[1] = state.Declarations[1] with { Required = true };
        if (defect == "category") { var clause = state.Declarations[2].ClauseReferences.Last(); state.Declarations[2].ClauseReferences.Remove(clause); state.Declarations[0].ClauseReferences.Add(clause); }
        if (defect == "foreign") state.References[0] = state.References[0] with { Owner = "foreign" };
        if (defect == "identity") state.BehaviorPlan!.Workflows[0].Outputs[0] = state.BehaviorPlan.Workflows[0].Outputs[0] with { DeclarationId = "other" };
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStageOneDeclarations(state));
    }

    [Fact]
    public void ReasoningAndCaseReportsDeduplicateReceiptsAndKeepUnknownUsage()
    {
        var call = new PlanningRequestAccounting { Id = "request", Evidence = "receipt", InputTokens = 30, OutputTokens = 100 };
        var state = new PlanningSnapshot { RequestAccounting = [call, call], ArtifactHash = "behavior" };
        var receipts = new Dictionary<string, LLMResponse?> { ["request"] = new() { Usage = JsonNode.Parse("""{"completion_tokens_details":{"reasoning_tokens":80}}""")!.AsObject() } };
        var report = ProgressiveReport.Build(state, receipts);
        Assert.Equal(80, report["reasoningTokens"]!.GetValue<long>()); Assert.Null(report["finalArtifactHash"]);
        Assert.True(JsonNode.DeepEquals(report, ProgressiveReport.Build(JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!, receipts)));
        receipts["request"]!.Usage = null;
        Assert.Null(ProgressiveReport.Build(state, receipts)["reasoningTokens"]);
        var failed = ProgressiveReport.ExecutionCase("case", new JsonObject { ["report"] = new JsonObject { ["passed"] = false }, ["privateFailure"] = "PRIVATE" });
        Assert.Equal("failed", failed["status"]!.ToString()); Assert.DoesNotContain("PRIVATE", failed.ToJsonString());
        Assert.Equal("not_run", ProgressiveReport.ExecutionCase("case", null)["status"]!.ToString());
        Assert.Equal("passed", ProgressiveReport.ExecutionCase("case", new JsonObject { ["passed"] = true })["status"]!.ToString());
    }

    [Fact]
    public void ConstraintReportsExposeOnlyOwnedIdentifiersAndKeepHistoricalAttributionUnknown()
    {
        var state = new PlanningSnapshot
        {
            Obligations = [new("constraint", ["reference"], "business_decision", "declaration_constraint", true)
                { Grounding = new(PlanningSourceAuthority.RequestedBehavior, PlanningSourceSemanticRole.Declaration, "clause", null, "proof") }],
            DeclarationAssignments = [new("constraint", "modifier_of", "output", null, null, "unspecified", null),
                new("historical", "modifier_of", "input", null, null, "unspecified", null)]
        };
        state.Request.Prompt = "PRIVATE";
        var report = ProgressiveReport.Build(state, new Dictionary<string, LLMResponse?>());
        Assert.Equal("output", report["declarationModifiers"]![0]!["targetId"]!.ToString());
        Assert.Equal("declaration_constraint", report["declarationModifiers"]![0]!["kind"]!.ToString());
        Assert.Null(report["declarationModifiers"]![1]!["kind"]);
        Assert.DoesNotContain("PRIVATE", report.ToJsonString());
        Assert.True(JsonNode.DeepEquals(report, ProgressiveReport.Build(JsonSerializer.Deserialize(JsonSerializer.Serialize(state,
            PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!, new Dictionary<string, LLMResponse?>())));
    }

    [Fact]
    public void OperationReportsKeepProofAndReuseSeparateFromUninstrumentedHistory()
    {
        var state = new PlanningSnapshot { OperationAdmissionFingerprint = "set", Request = new() { Prompt = "PRIVATE" } };
        state.Obligations.Add(new("action", ["anchor"], "workflow", "local_processing", true)
        { OperationAdmission = new(1, "action", "anchor", null,
            [new("first", "primary", "anchor", "local_processing", true, null, null),
             new("second", "rules", "rule_anchor", "local_processing", true, "action", null)], "evidence", "proof") });
        var report = ProgressiveReport.Build(state, new Dictionary<string, LLMResponse?>());
        Assert.Equal(1, report["canonicalOperationCount"]!.GetValue<int>());
        Assert.Equal(1, report["canonicalOperations"]![0]!["evidenceReuseCount"]!.GetValue<int>());
        Assert.DoesNotContain("PRIVATE", report.ToJsonString());
        Assert.True(JsonNode.DeepEquals(report, ProgressiveReport.Build(JsonSerializer.Deserialize(JsonSerializer.Serialize(state,
            PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!, new Dictionary<string, LLMResponse?>())));
        Assert.Null(ProgressiveReport.Build(new(), new Dictionary<string, LLMResponse?>())["canonicalOperationCount"]);
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireStageOneOperations(state));
    }

    private static PlanningSnapshot ThresholdCandidate() => new()
    {
        Revision = 20, Status = PlanningStatus.Generating, CurrentPhase = PlanningPhase.Repair,
        Construction = new() { Candidates = [new()
        {
            WorkflowKey = "main", Targets = [new() { Id = "new_hole", CanonicalLocation = "/workflows/@main/steps/@action/input/members/@result/value" }],
            Diagnostics = [new("BUSINESS_INPUT_BINDING_MISSING", "/assignments/new_hole", "PRIVATE_MESSAGE", Rule: "input:threshold")]
        }] }
    };

    [Fact]
    public void ThresholdObserverAllowsTheInitialFindingWithoutChangingThePlanner()
    {
        var state = ThresholdCandidate(); var stage = new JsonObject { ["status"] = "running" };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        ProgressiveRules.ObserveThresholdRepair(stage, state, new Dictionary<string, LLMResponse?>());
        Assert.Equal("running", stage["status"]!.ToString()); Assert.Equal("awaiting_repair", stage["thresholdRepair"]!["status"]!.ToString());
        Assert.Equal(state.Construction.Candidates[0].Targets[0].CanonicalLocation, stage["thresholdRepair"]!["location"]!.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot));
        Assert.DoesNotContain("PRIVATE_MESSAGE", stage.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThresholdObserverContinuesAfterValidatedResolution(bool deterministic)
    {
        var state = ThresholdCandidate(); var stage = new JsonObject { ["status"] = "running" };
        var receipts = new Dictionary<string, LLMResponse?>();
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts);
        state.Construction.Candidates.Clear(); state.CurrentPhase = PlanningPhase.Construction; state.Revision++;
        if (!deterministic)
        {
            state.RequestAccounting.Add(new() { Id = "repair", Phase = PlanningPhase.Repair, WorkflowKey = "main", Evidence = "receipt" });
            receipts["repair"] = new() { Json = new JsonObject() };
        }
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts);
        Assert.Equal("running", stage["status"]!.ToString());
        Assert.Equal(deterministic ? "resolved_without_model" : "resolved", stage["thresholdRepair"]!["status"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedThresholdRepairFailureStopsBeforeAnotherAdvanceAndSurvivesRestart(bool rejected)
    {
        var state = ThresholdCandidate(); var stage = new JsonObject { ["status"] = "running" };
        var receipts = new Dictionary<string, LLMResponse?>();
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts);
        stage = JsonNode.Parse(stage.ToJsonString())!.AsObject();
        state.RequestAccounting.Add(new() { Id = "repair", Phase = PlanningPhase.Repair, WorkflowKey = "main", Evidence = "receipt" });
        receipts["repair"] = new() { Json = new JsonObject() }; state.Revision++;
        if (rejected)
        {
            state.Construction.Candidates.Clear();
            state.Attempts.Add(new("hash", PlanningPhase.Repair, 1, false, [new("REPAIR_REGRESSION", "main", "PRIVATE_DIAGNOSTIC")]));
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts);
        Assert.Equal("blocked", stage["status"]!.ToString()); Assert.Equal("PlannerModelConvergence", stage["harnessFailure"]!.ToString());
        Assert.Equal("BENCHMARK_THRESHOLD_REPAIR_FAILED", stage["failureCode"]!.ToString());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot));
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireOpen(stage));
        var retained = stage.ToJsonString(); ProgressiveRules.ObserveThresholdRepair(stage, state, receipts); Assert.Equal(retained, stage.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownRepairReceiptStopsWithoutClaimingSemanticFailure(bool providerStop)
    {
        var state = ThresholdCandidate(); var stage = new JsonObject { ["status"] = "running" };
        ProgressiveRules.ObserveThresholdRepair(stage, state, new Dictionary<string, LLMResponse?>());
        state.RequestAccounting.Add(new() { Id = "pending", Phase = PlanningPhase.Repair, WorkflowKey = "main", Evidence = providerStop ? "unverifiable" : "reserved" });
        if (providerStop) state.TechnicalStop = new("LLM_PROVIDER_SERVICEUNAVAILABLE", "repair", "$", true);
        ProgressiveRules.ObserveThresholdRepair(stage, state, new Dictionary<string, LLMResponse?> { ["pending"] = null });
        Assert.Equal("blocked", stage["status"]!.ToString()); Assert.Equal("UnverifiableRequest", stage["harnessFailure"]!.ToString());
        Assert.Single(state.RequestAccounting); Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequireOpen(stage));
    }

    [Fact]
    public void ReceiptRecoveryAndOutputPartitionsWaitForNormalAssessment()
    {
        var state = ThresholdCandidate(); var stage = new JsonObject { ["status"] = "running" };
        var receipts = new Dictionary<string, LLMResponse?>();
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts);
        state.RequestAccounting.Add(new() { Id = "parent", Phase = PlanningPhase.Repair, WorkflowKey = "main", Evidence = "receipt" });
        receipts["parent"] = new() { CompletionStatus = "output_limit" };
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts); Assert.Equal("running", stage["status"]!.ToString());
        state.RequestAccounting.Add(new() { Id = "child", Phase = PlanningPhase.Repair, WorkflowKey = "main", Evidence = "receipt" });
        receipts["child"] = new() { Json = new JsonObject() }; state.Construction.PendingCalls.Add(new() { Id = "child" });
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts); Assert.Equal("running", stage["status"]!.ToString());
        state.Construction.PendingCalls.Clear(); state.Construction.Candidates.Clear();
        ProgressiveRules.ObserveThresholdRepair(stage, state, receipts); Assert.Equal("resolved", stage["thresholdRepair"]!["status"]!.ToString());
    }

    [Fact]
    public void ThresholdObservationUsesIssuedCoordinatesAndRuleRatherThanMessages()
    {
        var state = ThresholdCandidate(); var stage = new JsonObject { ["status"] = "running" };
        state.Construction.Candidates[0].Diagnostics[0] = new("BUSINESS_INPUT_BINDING_MISSING", "/assignments/new_hole", "threshold", Rule: "input:other");
        ProgressiveRules.ObserveThresholdRepair(stage, state, new Dictionary<string, LLMResponse?>());
        Assert.Null(stage["thresholdRepair"]);
    }

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
                new() { Id = "root", Origin = PlanningDecisionPageOrigin.SemanticCorrection, Correction = true, Gate = PlanningGates.Typed, PartitionChildren = ["child"], SourceDecisionIds = ["original"] },
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
        Assert.Null(lineage[0]!["sourceDecisionIds"]); Assert.Equal("original", lineage[1]!["sourceDecisionIds"]![0]!.ToString());
        Assert.Equal(2, lineage[3]!["partitionDepth"]!.GetValue<int>()); Assert.True(lineage[3]!["singletonOutputLimit"]!.GetValue<bool>());
        Assert.Equal(1, report["requestsByPhase"]![0]!["repairReservations"]!.GetValue<int>()); // Never refund old charges.
        Assert.DoesNotContain("PRIVATE_", report.ToJsonString());
    }

    [Fact]
    public void EscalationReportingIsReplaySafeAndSeparateFromSemanticRepairs()
    {
        var proof = new PlanningOutputBudgetEscalation("parent", "request", "hash", "receipt", "choice", "semantic", "evidence");
        var page = new PlanningDecisionPage { Id = "escalation", Origin = PlanningDecisionPageOrigin.OutputBudgetEscalation,
            ParentId = "parent", OutputBudgetEscalation = proof, EffectiveOutputTokens = 16384, RequestId = "child" };
        var accounting = new PlanningRequestAccounting { Id = "child", OutputBudgetEscalation = proof, EffectiveOutputTokens = 16384, Repair = false };
        var state = new PlanningSnapshot { DecisionPages = [new() { Id = "parent", Origin = PlanningDecisionPageOrigin.OutputPartition,
            ParentId = "root" }, new() { Id = "root", Origin = PlanningDecisionPageOrigin.Initial }, page, page], RequestAccounting = [accounting, accounting] };
        var receipts = new Dictionary<string, LLMResponse?> { ["child"] = new() { CompletionStatus = "output_limit" } };
        var report = ProgressiveReport.Build(state, receipts);
        Assert.Equal(1, report["outputEscalations"]!.GetValue<int>()); Assert.Equal(0, report["semanticCorrectionPages"]!.GetValue<int>());
        Assert.Equal(1, report["pageLineage"]![2]!["partitionDepth"]!.GetValue<int>());
        Assert.Equal(16384, report["pageLineage"]![2]!["effectiveOutputTokens"]!.GetValue<int>());
        Assert.Equal("output_limit", report["pageLineage"]![2]!["completionStatus"]!.ToString());
        Assert.Equal("request", report["pageLineage"]![2]!["parentRequestId"]!.ToString());
        Assert.Equal(0, report["requestsByPhase"]![0]!["repairReservations"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(report, ProgressiveReport.Build(JsonSerializer.Deserialize(JsonSerializer.Serialize(state,
            PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!, receipts)));
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
