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

    private sealed class UnavailableCatalog : ILLMModelCatalog
    {
        internal int Calls;
        public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
        {
            Calls++;
            throw new HttpRequestException("Synthetic catalog 404", null, System.Net.HttpStatusCode.NotFound);
        }
    }

}
