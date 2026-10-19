using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AutomaticFailureRevisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewSessionsCapturePolicyWithoutChangingOldSessions()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        using var enabled = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: new() { BackgroundProcessingEnabled = false });
        var first = await enabled.StartAsync("enabled", "Return a greeting", false, Ct);
        Assert.True(first.Request.Options["automatic_correction"]!.GetValue<bool>());
        using var disabled = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(),
            settings: new() { BackgroundProcessingEnabled = false, AutomaticCorrectionEnabled = false });
        var second = await disabled.StartAsync("disabled", "Return a greeting", false, Ct);
        Assert.False(second.Request.Options["automatic_correction"]!.GetValue<bool>());
        Assert.True((await disabled.GetAsync(first.Request.SessionId, Ct))!.Request.Options["automatic_correction"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("large", false)]
    [InlineData("large", true)]
    [InlineData("historical", false)]
    public async Task FailedExecutionGetsOneUnapprovedDraftWithExactAcceptedRequirementsAndNoSave(string sourcePreset, bool stricterHost)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var runtime = new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask);
        var original = new PlanningSession { IntentVersion = 2, Request = new() { TenantId = "planning-tests", Name = "existing", Prompt = "Return exact data",
                MaxModelCalls = 8, MaxReplanAttempts = 2, Generation = new("medium", 12000, 8192) },
            Requirements = PlanningCorpus.Requirements("local"), Plan = PlanningCorpus.LiteralResult(), Revision = 7, ModelCalls = 4, ReplanAttempts = 2,
            SavedAgentId = "saved", Status = PlanningStatus.Saved };
        if (sourcePreset != "ordinary") original.Request.Options["construction_preset"] = "large";
        if (sourcePreset == "large")
        {
            original.Construction = new();
            original.Request.Policy = AgentPlanningPolicy.Create();
            original.Request.Policy.MaxStepsTotal = 5_000; original.Request.Policy.MaxStepsPerWorkflow = 5_000; original.Request.Policy.MaxWorkflows = 1_024;
        }
        original.Catalog = await runtime.DiscoverAsync(original.Request, Ct);
        original.Graph = new TaskPlanCompiler().Compile(original.Plan, original.Catalog).Graph!;
        original.Yaml = new PlanningGraphCompiler().Compile(original.Graph, original.Catalog, original.Request.Name);
        original.ApprovedHash = original.ComputeArtifactHash();
        Assert.True(await fixture.Store.TrySaveAsync(original, null, Ct));
        var retained = JsonSerializer.Serialize(original, PlanningJsonContext.Default.PlanningSession);
        var writes = 0;
        var agents = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_get_by_name", new JsonObject { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "saved", ["workflow"] = original.Yaml } })
            .OnTool("agent_update", (_, _) => { writes++; throw new InvalidOperationException("No save is authorized"); });
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, settings: new()
            { BackgroundProcessingEnabled = false, LargeMaxModelCalls = stricterHost ? 100 : 384, LargeMaxReplanAttempts = stricterHost ? 8 : 64 });
        var evidence = Evidence(); var hash = PlanningGraphCompiler.Fingerprint(original.Yaml);
        var drafts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.StartFailureRevisionAsync("existing", "Return exact data", "failed-run", hash, evidence, "interactive", Ct)));
        Assert.Single(drafts.Select(d => d.Request.SessionId).Distinct());
        var draft = drafts[0];
        Assert.Null(draft.ApprovedHash); Assert.Equal(0, draft.ModelCalls); Assert.Equal(0, draft.ReplanAttempts);
        Assert.Equal(sourcePreset == "large" ? "large" : "ordinary", draft.Request.Options["construction_preset"]!.ToString());
        Assert.Equal(sourcePreset == "large" ? stricterHost ? 100 : 384 : 64, draft.Request.MaxModelCalls);
        Assert.Equal(sourcePreset == "large" ? stricterHost ? 8 : 64 : 4, draft.Request.MaxReplanAttempts);
        Assert.Equal(draft.Request.MaxModelCalls, draft.Request.Options["llm_budget"]!["max_calls"]!.GetValue<int>());
        Assert.Equal(32000, draft.Request.Generation.MaxInputTokensPerRequest); Assert.Equal(32768, draft.Request.Generation.MaxOutputTokens);
        Assert.False(draft.Request.Options.ContainsKey("business_judge"));
        Assert.Equal(JsonSerializer.Serialize(original.Requirements, PlanningJsonContext.Default.PlanningRequirements), JsonSerializer.Serialize(draft.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(JsonSerializer.Serialize(original.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(draft.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(JsonSerializer.Serialize(original.Request.Policy, PlanningJsonContext.Default.PlanningPolicy), JsonSerializer.Serialize(draft.Request.Policy, PlanningJsonContext.Default.PlanningPolicy));
        Assert.Equal(retained, JsonSerializer.Serialize(await fixture.Store.LoadAsync("planning-tests", original.Request.SessionId, Ct), PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(0, writes);
        var restarted = await service.StartFailureRevisionAsync("existing", "Return exact data", "failed-run", hash, evidence, "interactive", Ct);
        Assert.Equal(draft.Request.SessionId, restarted.Request.SessionId);
        Assert.Equal(2, (await fixture.Store.ListAsync("planning-tests", Ct)).Count);
        Assert.Null(await fixture.Store.LoadAsync("other-tenant", draft.Request.SessionId, Ct));
        await Assert.ThrowsAsync<PlanningConflictException>(() => service.SubmitAsync(draft.Request.SessionId,
            new() { Kind = "save", ExpectedRevision = draft.Revision }, Ct));
    }

    [Fact]
    public async Task AuthoredYamlPausesForIntentWithoutInferenceOrImport()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        const string yaml = "version: 1\nworkflows: {main: {steps: []}}";
        var agents = new FakeMcpSession("GnOuGo.Agent.Mcp").OnTool("agent_get_by_name", new JsonObject
        { ["success"] = true, ["agent"] = new JsonObject { ["id"] = "authored", ["workflow"] = yaml } });
        using var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, settings: new() { BackgroundProcessingEnabled = false });
        var draft = await service.StartFailureRevisionAsync("authored", "Preserve behavior", "failed", PlanningGraphCompiler.Fingerprint(yaml), Evidence(), "interactive", Ct);
        Assert.DoesNotContain("YAML", draft.Request.RevisionContext!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("original authored workflow remains unchanged", draft.Request.RevisionContext);
        Assert.DoesNotContain("YAML", Assert.Single(draft.GetQuestions()).Question, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlanningStatus.Clarification, draft.Status); Assert.Null(draft.Request.Baseline); Assert.Null(draft.Plan);
        Assert.Single(draft.GetQuestions()); Assert.Equal(0, draft.ModelCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartFailureRevisionAsync("authored", "Preserve behavior", "other", "stale", Evidence(), "interactive", Ct));
    }

    [Theory]
    [InlineData("CONTRACT_UNSATISFIED", true)]
    [InlineData("LLM_BUDGET_UNVERIFIABLE", false)]
    [InlineData("LLM_BUDGET_EXCEEDED", false)]
    [InlineData("MCP_CONNECTION_ERROR", false)]
    [InlineData("MCP_TIMEOUT", false)]
    [InlineData("RUN_NEEDS_RECONCILIATION", false)]
    [InlineData("PERMISSION_DENIED", false)]
    [InlineData("INTERNAL_ERROR", false)]
    [InlineData("EXPR_PARSE", false)]
    public void ClassificationUsesCauseInsteadOfOfferingAnLlmForEveryFailure(string code, bool expected)
    {
        var evidence = Evidence();
        Assert.Equal(expected, SmartFlowService.IsCompositionFailure(new(code, "Retained failure", code, evidence["details"])));
        evidence["details"]!["classification"] = "mapping_request_admission";
        Assert.False(SmartFlowService.IsCompositionFailure(new(code, "Retained failure", code, evidence["details"])));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("budget")]
    [InlineData("depth")]
    [InlineData("transport")]
    public void NestedUncertaintyAndTruncatedEvidenceDoNotTriggerCorrection(string cause)
    {
        var details = Evidence()["details"]!.DeepClone().AsObject();
        if (cause == "unknown") details["inner"] = new JsonObject { ["dispatch_status"] = "unknown" };
        else if (cause == "budget") details["inner"] = new JsonObject { ["error_code"] = "LLM_BUDGET_EXCEEDED" };
        else if (cause == "transport") details["inner"] = new JsonObject { ["code"] = "MCP_TIMEOUT" };
        else
        {
            var node = details;
            for (var i = 0; i < 8; i++) { var next = new JsonObject(); node["inner"] = next; node = next; }
        }
        Assert.False(SmartFlowService.IsCompositionFailure(new("CONTRACT_UNSATISFIED", "failure", "type", details)));
    }

    private static JsonObject Evidence() => new() { ["error_code"] = "CONTRACT_UNSATISFIED", ["details"] = new JsonObject
    { ["failed_step_id"] = "extract", ["mapping_result_path"] = "$/status/success", ["mapping_failure_kind"] = "program", ["mapping_failure_reason"] = "ungrounded_scalar", ["mapping_attempts"] = 2 } };
}
