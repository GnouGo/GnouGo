using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ApplicationLlmBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static ApplicationLlmBudgetPolicy Policy(PlanningPersistenceTests.StoreFixture fixture, string? mode = null)
        => new(fixture.Records, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApplicationLlmBudget:Mode"] = mode }).Build(),
            Options.Create(new OpenTelemetrySettings { TenantId = "planning-tests" }), Options.Create(new WorkflowPlanningBudgetSettings()));
    internal static LocalOperatorAccess Access(bool authorized)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http"; context.Request.Host = new("127.0.0.1:12345");
        if (authorized) context.User = new(new ClaimsIdentity([new Claim(LocalOperatorAccess.Claim, "true")], "test"));
        return new(new HttpContextAccessor { HttpContext = context }, new ServiceCollection()
            .AddSingleton(Options.Create(new TypedWorkflowPlanningSettings())).BuildServiceProvider());
    }
    [Fact]
    public async Task UnlimitedDefaultAndProtectedPreferenceSurviveRestartWithoutChangingOldCapture()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var policy = Policy(fixture); await policy.InitializeAsync(Ct);
        Assert.Equal("unlimited", policy.Mode);
        var original = policy.Capture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => policy.SetAsync("limited", Access(false), Ct));
        await policy.SetAsync("limited", Access(true), Ct);
        var restarted = Policy(fixture); await restarted.InitializeAsync(Ct);
        Assert.Equal("limited", restarted.Mode); Assert.Equal("operator_preference", restarted.Capture()["origin"]!.ToString());
        Assert.Equal("unlimited", original["mode"]!.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() => policy.SetAsync("infinite", Access(true), Ct));
        var explicitHost = Policy(fixture, "limited"); await explicitHost.SetAsync("unlimited", Access(true), Ct);
        var reopened = Policy(fixture, "limited"); await reopened.InitializeAsync(Ct); Assert.Equal("unlimited", reopened.Mode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrePreferenceAuthorityCapturesKeepTheirExactShapeAndOriginalFiniteAllowance(bool tamper)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = Options.Create(new TypedWorkflowPlanningSettings());
        var oldOwner = new PlanningHostPolicy(fixture.Records, settings);
        var state = new PlanningSession { Request = new() { TenantId = "planning-tests", Prompt = "Return 42",
            Options = new() { ["llm_budget"] = new JsonObject { ["max_estimated_cost"] = new JsonObject { ["amount"] = 50, ["currency"] = "EUR" } } } } };
        await oldOwner.CaptureAsync(state, DiagnosticPlanningTests.ModelOptions(), "diagnostic", new("operator"), Ct);
        const string collection = "agent-planning-host-authority-v1";
        var original = (await fixture.Records.GetAsync(collection, "planning-tests", state.Request.SessionId, EfPlanningSessionStore.Author, Ct))!;
        // This is the exact pre-feature document shape, with no new optional key.
        var historical = JsonNode.Parse(original.Value)!.AsObject(); historical.Remove("application_llm_budget");
        await fixture.Records.UpsertAsync(collection, "planning-tests", state.Request.SessionId, historical.ToJsonString(), EfPlanningSessionStore.Author, Ct);
        var retained = (await fixture.Records.GetAsync(collection, "planning-tests", state.Request.SessionId, EfPlanningSessionStore.Author, Ct))!.Value;
        var currentOwner = new PlanningHostPolicy(fixture.Records, settings, Policy(fixture));
        if (tamper)
        {
            state.Request.Options["application_llm_budget"] = Policy(fixture).Capture();
            await Assert.ThrowsAsync<PlanningConflictException>(() => currentOwner.VerifyAsync(state, Ct));
        }
        else
        {
            await currentOwner.VerifyAsync(state, Ct); Assert.False(state.Request.Options.ContainsKey("application_llm_budget"));
            Assert.Equal(50, state.Request.Options["llm_budget"]!["max_estimated_cost"]!["amount"]!.GetValue<int>());
        }
        Assert.Equal(retained, (await fixture.Records.GetAsync(collection, "planning-tests", state.Request.SessionId, EfPlanningSessionStore.Author, Ct))!.Value);
    }

    [Theory]
    [InlineData("unlimited", false, false)]
    [InlineData("unlimited", true, true)]
    [InlineData("limited", false, true)]
    [InlineData("limited", true, true)]
    public async Task ChatBridgeKeepsExplicitWorkflowLimitsAndApplicationFundingIndependent(string mode, bool explicitLimits, bool blocked)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var policy = Policy(fixture, mode);
        var authority = new PlanningHostPolicy(fixture.Records, Options.Create(new TypedWorkflowPlanningSettings()), policy);
        var options = DiagnosticPlanningTests.ModelOptions();
        options.ModelOverrides["openai/gpt-6-luna"] = new() { Id = "gpt-6-luna", ProviderType = "openai",
            Pricing = new() { Currency = "USD", InputPer1MTokens = 30, OutputPer1MTokens = 180 } };
        var factory = new GnOuGo.Flow.Integrations.Planning.WorkflowPlanningRuntimeFactory(fixture.Records,
            Path.Combine(fixture.Root, "budget-leases"), new TestAuthority(authority, options));
        var context = new GnOuGo.Flow.Core.Runtime.StepExecutionContext
        {
            Engine = new() { ModelUsageCostEstimator = new GnOuGo.Flow.Integrations.ModelMetadataUsageCostEstimator(options), ExchangeRateProvider = new TestExchangeRateProvider() },
            Limits = new() { TenantId = "planning-tests", RunId = "chat-budget" }, Data = new(),
            Step = new() { Source = new GnOuGo.Flow.Core.Models.StepDef { Id = "plan", Type = "workflow.plan" } }
        };
        var initial = new PlanningSession { Request = new() { TenantId = "planning-tests", Prompt = "Return 42" } };
        if (explicitLimits) initial.Request.Options["llm_budget"] = new JsonObject { ["max_calls"] = 1 };
        await using var owned = await factory.OpenAsync(context, initial, Ct);
        var admission = (IPlanningConstructionAdmission)owned.Runtime;
        var request = new GnOuGo.Flow.Core.Runtime.LLMRequest { Model = "gpt-6-luna", Provider = "openai", MaxTokens = 128000 };
        if (blocked)
        {
            var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => admission.AdmitConstructionAsync(request, 16000, [128000, 128000, 128000], Ct));
            Assert.Equal("LLM_BUDGET_EXCEEDED", error.Code); Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        }
        else await admission.AdmitConstructionAsync(request, 16000, [128000, 128000, 128000], Ct);
        if (explicitLimits) Assert.Equal(1, owned.Session.Request.Options["llm_budget"]!["max_calls"]!.GetValue<int>());
        await authority.VerifyAsync(owned.Session, Ct);
    }
    private sealed class TestAuthority(PlanningHostPolicy policy, GnOuGo.AI.Core.LLMOptions options) : IPlanningHostPolicy
    {
        public Task CaptureAsync(PlanningSession session, CancellationToken ct) => policy.CaptureAsync(session, options, "diagnostic", new("operator"), ct);
        public Task CaptureAsync(PlanningSession session, PlanningSession creation, CancellationToken ct) => policy.CaptureAsync(session, options, "diagnostic", new("operator"), ct, creation);
        public Task VerifyAsync(PlanningSession session, CancellationToken ct) => policy.VerifyAsync(session, ct);
        public Task VerifyCreationAsync(PlanningSession session, CancellationToken ct) => policy.VerifyCreationAsync(session, ct);
    }

    [Theory]
    [InlineData("limited", false)]
    [InlineData("unlimited", false)]
    [InlineData("unlimited", true)]
    public async Task CapturedFiftyEuroHighTariffCaseUsesRealServerAdaptersAndSurvivesReload(string mode, bool missingPricing)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var budgetPolicy = Policy(fixture, mode); await budgetPolicy.InitializeAsync(Ct);
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false, DiagnosticEnabled = true };
        var authority = new PlanningHostPolicy(fixture.Records, Options.Create(settings), budgetPolicy);
        var options = DiagnosticPlanningTests.ModelOptions(); options.DefaultModel = "private-sol-deployment";
        Assert.True(GnOuGo.AI.Core.ModelMetadataCatalog.TryGetBuiltin("openai", "gpt-6.1-sol", out var metadata));
        var captured = GnOuGo.AI.Core.ModelMetadataCatalog.Clone(metadata); captured.Id = options.DefaultModel;
        captured.Pricing = new() { Currency = "USD", InputPer1MTokens = 30, OutputPer1MTokens = 180, CachedInputPer1MTokens = 3 };
        if (missingPricing) captured.Pricing = null;
        options.ModelOverrides["openai/" + options.DefaultModel] = captured;
        using var model = new JournaledPlanningModel(new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel(), modelOptions: options);
        using var server = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model, settings, authority, options);
        var state = await server.StartAsync("high-tariff-" + mode, "Return the required public numeric result named result with value 42.", false, Ct,
            thresholdMode: "diagnostic", diagnosticGrant: new("operator"));
        Assert.Equal(mode, state.Request.Options["application_llm_budget"]!["mode"]!.ToString());
        var frozen = state.Request.Options.ToJsonString();
        await budgetPolicy.SetAsync(mode == "limited" ? "unlimited" : "limited", Access(true), Ct);
        for (var n = 0; n < 25 && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status); n++)
        {
            state = await server.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
            state = (await fixture.Store.LoadAsync("planning-tests", state.Request.SessionId, Ct))!;
            await authority.VerifyAsync(state, Ct);
        }
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(frozen), state.Request.Options));
        if (mode == "limited")
        {
            Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(model.Requests);
            var failure = Assert.Single(state.Diagnostics, d => d.Code == "LLM_BUDGET_EXCEEDED").BudgetFailure!;
            Assert.Equal("not_started", failure.DispatchStatus); Assert.Equal(50, failure.Allowance);
            Assert.True(await server.CanStartWithCurrentSettingsAsync(state, Ct));
            state.Usage = new() { Calls = 1 }; Assert.False(await server.CanStartWithCurrentSettingsAsync(state, Ct));
        }
        else
        {
            Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message))); Assert.True(state.ModelCalls > 1);
            Assert.All(model.Requests, r => Assert.Equal(128000, r.MaxTokens));
            Assert.Contains(state.InspectionEvents!, e => e.Phase == "construction_global_judge");
            Assert.Equal(!missingPricing, state.Usage!.CostEstimateAvailable);
            if (missingPricing) Assert.Equal("pricing_unavailable", state.Usage.CostUnavailableReason);
            else Assert.True(state.Usage.EstimatedCost > 0);
            Assert.Null(state.ApprovedHash);
        }
    }
}
