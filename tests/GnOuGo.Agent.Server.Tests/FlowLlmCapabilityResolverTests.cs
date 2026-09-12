using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class FlowLlmCapabilityResolverTests
{
    internal static LLMOptions ConfiguredOptions() => new()
    {
        DefaultProvider = "deployment", DefaultModel = "reviewed",
        Models = { ["deployment"] = new() { Type = "openai", Url = "https://gateway.example/deployments/model", ApiVersion = "configured", Issuer = "https://identity.example" } },
        ModelOverrides =
        {
            ["reviewed"] = new() { Pricing = new() { InputPer1MTokens = 1, OutputPer1MTokens = 1 } },
            ["openai/reviewed"] = new() { Id = "reviewed", ProviderType = "openai", Capabilities = new()
            { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["low", "medium"], SupportsStructuredOutput = true } }
        }
    };

    [Fact]
    public async Task InjectedResolverNeverUsesDiscoveryAndSeesEditsImmediately()
    {
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(ConfiguredOptions());
        var catalog = new UnavailableCatalog();
        using var provider = new ServiceCollection().AddSingleton(store).AddSingleton<ILLMModelCatalog>(catalog)
            .AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>().BuildServiceProvider();
        var resolver = provider.GetRequiredService<ILLMCapabilityResolver>();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(["low", "medium"], await resolver.SupportedReasoningLevelsAsync(null, "", ct));
        Assert.True(await resolver.SupportsStructuredOutputAsync("deployment", "reviewed", ct));
        store.UpsertModelOverride("openai/reviewed", new() { Capabilities = new() { SupportsReasoningEffort = false, SupportedReasoningEfforts = [], SupportsStructuredOutput = false } });
        Assert.Empty((await resolver.SupportedReasoningLevelsAsync(null, "", ct))!);
        Assert.False(await resolver.SupportsStructuredOutputAsync(null, "", ct));
        Assert.Equal(0, catalog.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.SupportedReasoningLevelsAsync(null, "", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.SupportsStructuredOutputAsync(null, "", cancelled.Token));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("copilot")]
    public async Task ProviderAliasesAndNamespacedModelsUseTheSameMetadataAsDispatch(string type)
    {
        var options = ConfiguredOptions();
        options.Models["deployment"].Type = type;
        options.ModelOverrides.Clear();
        options.ModelOverrides[type + "/reviewed"] = new() { Capabilities = new() { SupportsStructuredOutput = true } };
        var resolver = new FlowLlmCapabilityResolver(SmartFlowTestFactory.CreateRuntimeOptionsStore(options));
        var expected = new RoutingLLMClient(options, []).ResolveDeclaredCapabilities("deployment", "openai/reviewed");
        Assert.True(expected!.SupportsStructuredOutput);
        Assert.Equal(expected.SupportsStructuredOutput, await resolver.SupportsStructuredOutputAsync("deployment", "openai/reviewed", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlannerJournalsIntentRequestsOnlyWhenLocalMetadataProvesReasoning(bool declared)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var options = ConfiguredOptions();
        if (!declared) options.ModelOverrides.Clear();
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        var catalog = new UnavailableCatalog();
        using var provider = new ServiceCollection().AddSingleton(store).AddSingleton<ILLMModelCatalog>(catalog)
            .AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>().BuildServiceProvider();
        var model = new FirstIntentClient();
        var runtime = new SecureWorkflowRuntimeFactory(store, new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options),
            llmClientOverride: model, mcpClientFactoryOverride: new FakeMcpClientFactory(PlanningSessionLifecycleTests.AgentCatalog()),
            llmCapabilityResolver: provider.GetRequiredService<ILLMCapabilityResolver>());
        using var service = new PlanningSessionService(fixture.Store, fixture, fixture.Records, runtime, new TypedWorkflowPlanner(), new TestExchangeRateProvider(),
            Options.Create(new WorkflowPlanningBudgetSettings()), Options.Create(new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false }),
            Options.Create(new OpenTelemetrySettings { TenantId = "planning-tests" }), NullLogger<PlanningSessionService>.Instance);
        var ct = TestContext.Current.CancellationToken;
        var state = await service.StartAsync("Local metadata", "Return a greeting", false, ct);
        state = await service.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, ct);
        await using var db = fixture.CreateDbContext();
        Assert.True(declared ? model.Calls > 0 : model.Calls == 0, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(model.Calls, await db.Calls.CountAsync(ct));
        Assert.Equal(0, catalog.Calls);
        var saved = (await fixture.Store.LoadAsync(state.Request.TenantId, state.Request.SessionId, ct))!;
        Assert.NotEmpty(saved.RequestAccounting);
        Assert.All(saved.RequestAccounting, accounting => Assert.Equal(declared ? "receipt" : "not_dispatched", accounting.Evidence));
        if (declared)
        {
            Assert.True(saved.Intent.Checked);
            Assert.Null(saved.TechnicalStop);
            Assert.Equal("low", model.Reasoning);
        }
        else Assert.Equal("MODEL_REASONING_UNPROVEN", saved.TechnicalStop!.Code);
    }

    [Fact]
    public async Task UnreadableMetadataUsesTheExistingTechnicalStopWithoutDispatch()
    {
        var options = ConfiguredOptions(); options.ModelMetadataFiles = [Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing")];
        var model = new FirstIntentClient();
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = model,
            LLMCapabilities = new FlowLlmCapabilityResolver(SmartFlowTestFactory.CreateRuntimeOptionsStore(options)) }, (_, _) => Task.CompletedTask);
        var state = new PlanningSnapshot { Request = new() { TenantId = "test", Prompt = "Return a greeting" } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal("MODEL_METADATA_UNAVAILABLE", state.TechnicalStop!.Code);
        Assert.Equal("not_dispatched", Assert.Single(state.RequestAccounting).Evidence);
        Assert.Equal(0, model.Calls);
        Assert.Empty(state.Construction.PendingCalls);
    }

    private sealed class UnavailableCatalog : ILLMModelCatalog
    {
        internal int Calls;
        public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
        {
            Calls++;
            throw new HttpRequestException("Synthetic catalog 404", null, System.Net.HttpStatusCode.NotFound);
        }
    }

    private sealed class FirstIntentClient : ILLMClient
    {
        internal int Calls;
        internal string? Reasoning;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Reasoning = request.Reasoning;
            // Source interpretation includes the request and host policy pages.
            // Stop after that phase; these synthetic receipts make no convergence claim.
            var response = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject()
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonArray())));
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
