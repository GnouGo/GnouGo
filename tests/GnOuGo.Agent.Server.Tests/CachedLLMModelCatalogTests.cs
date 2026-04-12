using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CachedLlmModelCatalogTests
{
    [Fact]
    public async Task ListModelsAsync_ReusesCachedEntry_ForSameProviderConfiguration()
    {
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
            }
        });

        var inner = new FakeModelCatalog()
            .Add("openai", new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new CachedLlmModelCatalog(
            inner,
            store,
            cache,
            new ModelCatalogCacheSettings { Enabled = true, AbsoluteExpirationSeconds = 30 },
            NullLogger<CachedLlmModelCatalog>.Instance);

        var first = await catalog.ListModelsAsync("openai", CancellationToken.None);
        var second = await catalog.ListModelsAsync("openai", CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ListModelsAsync_UsesSeparateCacheEntries_PerProvider()
    {
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret-a" },
                ["copilot"] = new() { Url = "https://models.github.ai/inference", Type = "copilot", ApiKey = "secret-b" }
            }
        });

        var inner = new FakeModelCatalog()
            .Add("openai", new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"))
            .Add("copilot", new LLMModelDescriptor("openai/gpt-4.1", "openai/gpt-4.1", "copilot", "github"));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new CachedLlmModelCatalog(
            inner,
            store,
            cache,
            new ModelCatalogCacheSettings { Enabled = true, AbsoluteExpirationSeconds = 30 },
            NullLogger<CachedLlmModelCatalog>.Instance);

        await catalog.ListModelsAsync("openai", CancellationToken.None);
        await catalog.ListModelsAsync("copilot", CancellationToken.None);
        await catalog.ListModelsAsync("openai", CancellationToken.None);
        await catalog.ListModelsAsync("copilot", CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task ListModelsAsync_InvalidatesCache_WhenProviderConfigurationChanges()
    {
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret-a" }
            }
        });

        var inner = new FakeModelCatalog()
            .Add("openai", new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new CachedLlmModelCatalog(
            inner,
            store,
            cache,
            new ModelCatalogCacheSettings { Enabled = true, AbsoluteExpirationSeconds = 30 },
            NullLogger<CachedLlmModelCatalog>.Instance);

        await catalog.ListModelsAsync("openai", CancellationToken.None);

        SmartFlowTestFactory.SetRuntimeOptions(store, new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret-b" }
            }
        });

        await catalog.ListModelsAsync("openai", CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
    }
}


