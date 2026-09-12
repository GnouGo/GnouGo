using GnOuGo.AI.Core;
using Xunit;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class DeclaredCapabilityTests
{
    [Fact]
    public async Task AdapterUsesDeclaredMetadataAndDispatchDefaultsWithoutTransport()
    {
        var options = new LLMOptions { DefaultProvider = "deployment", DefaultModel = "reviewed", Models = { ["deployment"] = new() { Type = "openai" } },
            ModelOverrides = { ["openai/reviewed"] = new() { Capabilities = new() { SupportsStructuredOutput = true, SupportsReasoningEffort = true, SupportedReasoningEfforts = ["low", "medium"] } } } };
        var adapter = new RoutingLLMClientAdapter(new RoutingLLMClient(options, []));
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await adapter.SupportsStructuredOutputAsync(null, "", ct));
        Assert.Equal(["low", "medium"], await adapter.SupportedReasoningLevelsAsync("deployment", "reviewed", ct));
        Assert.Null(await adapter.SupportsStructuredOutputAsync("deployment", "gpt-4o-mni", ct));
        Assert.Null(await adapter.SupportedReasoningLevelsAsync("deployment", "gpt-4o-mni", ct));
    }

    [Fact]
    public async Task ExplicitUnsupportedReasoningRemainsDistinctFromUnknown()
    {
        var options = new LLMOptions { DefaultProvider = "deployment", Models = { ["deployment"] = new() { Type = "openai" } },
            ModelOverrides = { ["openai/disabled"] = new() { Capabilities = new() { SupportsReasoningEffort = false, SupportsStructuredOutput = false } } } };
        var adapter = new RoutingLLMClientAdapter(new RoutingLLMClient(options, []));
        Assert.Empty((await adapter.SupportedReasoningLevelsAsync(null, "disabled", TestContext.Current.CancellationToken))!);
        Assert.False(await adapter.SupportsStructuredOutputAsync(null, "disabled", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.SupportedReasoningLevelsAsync(null, "disabled", cancelled.Token));
    }
}
