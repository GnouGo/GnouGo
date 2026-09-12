using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ProgressiveCampaignTests
{
    [Fact]
    public void ArchivedPreflightFailureCannotBeRestartedOrBypassed()
    {
        ProgressiveRules.RequirePreflight(null);
        ProgressiveRules.RequirePreflight(new());
        var manifest = new JsonObject { ["preflightStop"] = new JsonObject { ["code"] = "MODEL_CAPABILITY_DISCOVERY_FAILED" } };
        Assert.Throws<InvalidOperationException>(() => ProgressiveRules.RequirePreflight(JsonNode.Parse(manifest.ToJsonString())!.AsObject()));
    }

    [Fact]
    public async Task MissingModelCatalogFailsCapabilityPreflightWithoutGenerationOrRetry()
    {
        using var handler = new MissingCatalog();
        using var http = new HttpClient(handler);
        var options = new LLMOptions { DefaultProvider = "fixture", DefaultModel = "configured-model",
            Models = new() { ["fixture"] = new() { Type = "openai", Url = "https://provider.example/v1", ApiKey = "fixture-secret" } } };
        var store = new LLMRuntimeOptionsStore(Options.Create(options), NullLogger<LLMRuntimeOptionsStore>.Instance);
        var catalog = new RoutingLLMModelCatalog(options, [new OpenAiLLMProvider(http)]);
        var resolver = new FlowLlmCapabilityResolver(catalog, store, NullLogger<FlowLlmCapabilityResolver>.Instance);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => resolver.SupportedReasoningLevelsAsync("fixture", "configured-model", CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain("fixture-secret", error.ToString());
    }

    private sealed class MissingCatalog : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("""{"code":"NotFound","message":"The requested resource was not found","details":[]}""") });
        }
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
