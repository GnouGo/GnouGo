using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class FlowLlmCapabilityResolverTests
{
    [Fact]
    public async Task ResolveStructuredOutputAsync_UsesExactThenVersionlessFamilyAndPreservesExplicitFalse()
    {
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.5",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Type = "openai", Url = "https://example.test/v1", ApiKey = "secret" }
            }
        });
        var catalog = new FakeModelCatalog().Add(
            "openai",
            Descriptor("gpt-5.5", true),
            Descriptor("no-structured", false));
        var resolver = new FlowLlmCapabilityResolver(
            catalog,
            store,
            NullLogger<FlowLlmCapabilityResolver>.Instance);

        var exactFalse = await resolver.ResolveStructuredOutputAsync("openai", "no-structured", CancellationToken.None);
        var dated = await resolver.ResolveStructuredOutputAsync("openai", "gpt-5.5-2026-04-24", CancellationToken.None);
        var unknown = await resolver.ResolveStructuredOutputAsync("openai", "unknown-model", CancellationToken.None);

        Assert.False(exactFalse.Supported);
        Assert.Equal("exact_model", exactFalse.Source);
        Assert.True(dated.Supported);
        Assert.Equal("versionless_model_family", dated.Source);
        Assert.Null(unknown.Supported);
        Assert.Equal("unknown_model", unknown.Source);
    }

    [Fact]
    public async Task ResolveStructuredOutputAsync_CatalogFailure_UsesLocalClosestModelMetadata()
    {
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-5.5-2026-04-24",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Type = "openai", Url = "https://example.test/v1", ApiKey = "secret" }
            }
        });
        var resolver = new FlowLlmCapabilityResolver(
            new FailingModelCatalog(),
            store,
            NullLogger<FlowLlmCapabilityResolver>.Instance);

        var resolution = await resolver.ResolveStructuredOutputAsync(
            "openai",
            "gpt-5.5-2026-04-24",
            CancellationToken.None);

        Assert.True(resolution.Supported);
        Assert.Equal("local_version_family:catalog_error", resolution.Source);
    }

    private static LLMModelDescriptor Descriptor(string id, bool structuredOutput)
        => new(
            id,
            id,
            "openai",
            Capabilities: new ModelCapabilityMetadata { SupportsStructuredOutput = structuredOutput });

    private sealed class FailingModelCatalog : ILLMModelCatalog
    {
        public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(
            string provider,
            CancellationToken ct = default)
            => throw new HttpRequestException("Model catalogue endpoint returned 404.");
    }
}
