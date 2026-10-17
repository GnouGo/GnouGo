using GnOuGo.AI.Core;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkMetadataTests
{
    private static LLMOptions Options() => new() { DefaultProvider = "deployment", DefaultModel = "exact-snapshot",
        Models = { ["deployment"] = new() { Type = "openai" } },
        ModelOverrides = { ["openai/exact-snapshot"] = new() { MaxInputTokens = 9000, MaxOutputTokens = 8192, ContextWindowTokens = 16000 } } };

    [Fact]
    public async Task PersistedMetadataWinsWithoutChangingPinsOrOtherDeployments()
    {
        var options = Options(); var other = Options();
        var persisted = new Dictionary<string, LLMModelMetadata> { ["openai/exact-snapshot"] = new() { MaxInputTokens = 12000, MaxOutputTokens = 10000, ContextWindowTokens = 20000 } };
        KeyVaultBenchmarkModel.ApplyUserMetadata(options, persisted, "deployment", "exact-snapshot");
        var adapter = new RoutingLLMClientAdapter(new RoutingLLMClient(options, []));
        Assert.Equal(11808, await adapter.InputTokenAllowanceAsync("deployment", "exact-snapshot", 8192, TestContext.Current.CancellationToken));
        Assert.Equal(9000, other.ModelOverrides["openai/exact-snapshot"].MaxInputTokens);
        Assert.Null(await adapter.InputTokenAllowanceAsync("deployment", "different-snapshot", 8192, TestContext.Current.CancellationToken));
        Assert.Null(await adapter.InputTokenAllowanceAsync("deployment", "exact-snapshot", 10001, TestContext.Current.CancellationToken));
        Assert.Equal("", persisted["openai/exact-snapshot"].Id);
        Assert.Equal("deployment", options.DefaultProvider); Assert.Equal("exact-snapshot", options.DefaultModel);
    }

    [Theory]
    [InlineData("changed-provider", "exact-snapshot")]
    [InlineData("deployment", "changed-model")]
    public void MismatchedPinsFailBeforeApplyingOverrides(string provider, string model)
    {
        var options = Options(); var before = options.ModelOverrides;
        Assert.Throws<InvalidOperationException>(() => KeyVaultBenchmarkModel.ApplyUserMetadata(options, null, provider, model));
        Assert.Same(before, options.ModelOverrides);
    }
}
