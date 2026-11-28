using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmDefaultsResolverTests
{
    internal const string ConfiguredModel = "gpt-6.1-sol-2026-09-29";
    internal const string StaleModel = "gpt-5.5-2026-04-24";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, true, "keyvault_provider_configuration", ConfiguredModel)]
    [InlineData(false, true, "persisted_user_default", StaleModel)]
    [InlineData(false, false, "host_configuration", "gpt-4o-mini")]
    public async Task ResolutionHasOnePrecedenceAndSurvivesRestart(bool configured, bool persisted, string source, string expected)
    {
        var repository = new DefaultsRepository(persisted ? new("OPENAI", StaleModel, null, null) : new(null, null, null, null));
        using var services = new ServiceCollection().AddSingleton<IUserConfigRepository>(repository).BuildServiceProvider();
        var vault = new FakeKeyVaultRuntimeConfigStore();
        if (configured) vault.WithEffectiveOptions(OptionsFor(ConfiguredModel));
        for (var restart = 0; restart < 2; restart++)
        {
            var store = new LLMRuntimeOptionsStore(Options.Create(OptionsFor("gpt-4o-mini")), NullLogger<LLMRuntimeOptionsStore>.Instance);
            var resolver = new LlmDefaultsResolver(store, vault, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmDefaultsResolver>.Instance);
            await resolver.InitializeAsync(Ct);
            Assert.Equal(expected, store.Current.DefaultModel);
            Assert.Equal(source, store.DefaultModelSource);
            var billing = store.Current.Models["openai"].RequestPolicy;
            Assert.Equal("fast", billing.BillingServiceTier); Assert.True(billing.BillingRegionalProcessing); Assert.Equal("12h", billing.BillingCacheRetention);
        }
        Assert.Equal(persisted ? StaleModel : null, repository.Snapshot.DefaultLlmModel);
    }

    [Fact]
    public async Task RemovingConfiguredAndPersistedModelsRestoresTheHostDefaultRatherThanThePreviousResolution()
    {
        var repository = new DefaultsRepository(new("openai", StaleModel, null, null));
        using var services = new ServiceCollection().AddSingleton<IUserConfigRepository>(repository).BuildServiceProvider();
        var store = new LLMRuntimeOptionsStore(Options.Create(OptionsFor("gpt-4o-mini")), NullLogger<LLMRuntimeOptionsStore>.Instance);
        var vault = new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(OptionsFor(ConfiguredModel));
        var resolver = new LlmDefaultsResolver(store, vault, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmDefaultsResolver>.Instance);
        await resolver.InitializeAsync(Ct); Assert.Equal(ConfiguredModel, store.Current.DefaultModel);
        repository.Replace(new(null, null, null, null)); vault.WithEffectiveOptions(OptionsFor(""));
        await resolver.ResolveAsync(Ct);
        Assert.Equal("gpt-4o-mini", store.Current.DefaultModel); Assert.Equal("host_configuration", store.DefaultModelSource);
    }

    [Fact]
    public async Task MissingPersistedProviderCannotReplaceTheConfiguredModelsIdentifier()
    {
        using var services = new ServiceCollection().AddSingleton<IUserConfigRepository>(new DefaultsRepository(new("missing", StaleModel, null, null))).BuildServiceProvider();
        var store = new LLMRuntimeOptionsStore(Options.Create(OptionsFor("gpt-4o-mini")), NullLogger<LLMRuntimeOptionsStore>.Instance);
        var resolver = new LlmDefaultsResolver(store, new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(OptionsFor(ConfiguredModel)),
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmDefaultsResolver>.Instance);
        await resolver.InitializeAsync(Ct);
        Assert.Equal("openai", store.Current.DefaultProvider); Assert.Equal(ConfiguredModel, store.Current.DefaultModel);
    }

    [Fact]
    public async Task ChatAndNewWorkflowRuntimesUseTheSameResolutionWhileExistingRuntimeRemainsCaptured()
    {
        using var services = new ServiceCollection().AddSingleton<IUserConfigRepository>(new DefaultsRepository(new("openai", StaleModel, null, null))).BuildServiceProvider();
        var options = OptionsFor(ConfiguredModel);
        var vault = new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options);
        var store = new LLMRuntimeOptionsStore(Options.Create(OptionsFor(StaleModel)), NullLogger<LLMRuntimeOptionsStore>.Instance);
        var resolver = new LlmDefaultsResolver(store, vault, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LlmDefaultsResolver>.Instance);
        var handler = new ModelHandler(); using var http = new HttpClient(handler);
        var chat = new DynamicRoutingLLMClientAdapter(http, store, NullLoggerFactory.Instance, defaults: resolver);
        await chat.CallAsync(new LLMRequest { Model = "", Prompt = "Return 42" }, Ct);
        Assert.Equal(ConfiguredModel, handler.Model);
        var factory = new SecureWorkflowRuntimeFactory(store, vault, defaults: resolver);
        await using var original = await factory.CreateAsync(Ct);
        Assert.Equal(ConfiguredModel, original.Options.DefaultModel);
        vault.WithEffectiveOptions(OptionsFor("gpt-6.1-sol"));
        await using var next = await factory.CreateAsync(Ct);
        Assert.Equal("gpt-6.1-sol", next.Options.DefaultModel);
        Assert.Equal(ConfiguredModel, original.Options.DefaultModel);
    }

    private static LLMOptions OptionsFor(string model)
    {
        var options = new LLMOptions { DefaultProvider = "openai", DefaultModel = model,
            Models = new(StringComparer.OrdinalIgnoreCase) { ["openai"] = new() { Type = "openai", Url = "https://controlled.invalid/v1",
                RequestPolicy = new() { BillingServiceTier = "fast", BillingRegionalProcessing = true, BillingCacheRetention = "12h" } } } };
        if (model == ConfiguredModel)
        {
            Assert.True(ModelMetadataCatalog.TryGetBuiltin("openai", "gpt-6.1-sol", out var metadata));
            var deployment = ModelMetadataCatalog.Clone(metadata); deployment.Id = model;
            options.ModelOverrides["openai/" + model] = deployment;
        }
        return options;
    }

    internal sealed class DefaultsRepository(UserConfigSnapshot snapshot) : IUserConfigRepository
    {
        public UserConfigSnapshot Snapshot => snapshot;
        internal void Replace(UserConfigSnapshot replacement) => snapshot = replacement;
        public Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default) => Task.FromResult(snapshot);
        public Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Resolution must not rewrite persisted user defaults.");
    }
    private sealed class ModelHandler : HttpMessageHandler
    {
        public string? Model { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Model = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["model"]!.ToString();
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"42"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""", Encoding.UTF8, "application/json") };
        }
    }
}
