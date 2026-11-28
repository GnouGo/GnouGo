using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class DeclaredCapabilityTests
{
    [Theory]
    [InlineData("gpt-6-astra", 7)]
    [InlineData("gpt-4o", 510)]
    [InlineData("gpt-6.1-terra", null)]
    public async Task VisionReservationUsesEachTransmittedImageAndExactModelRules(string model, int? expected)
    {
        var options = new LLMOptions { DefaultProvider = "deployment", Models = { ["deployment"] = new() { Type = "openai" } },
            ModelOverrides = { ["openai/" + model] = new() { Capabilities = new() { SupportsVision = true } } } };
        var adapter = new RoutingLLMClientAdapter(new RoutingLLMClient(options, []));
        WorkflowImage[] images = [new("a", "image/png", 32, 32, 100), new("b", "image/png", 64, 32, 180)];
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(expected, await adapter.ImageTokenUpperBoundAsync(null, model, images, ct));
        Assert.Equal(expected, await adapter.ImageTokenUpperBoundAsync(null, model, [images[0] with { Size = 2000 }, images[1]], ct));
    }

    [Fact]
    public async Task MappingAllowanceUsesOnlyExactLimitsAndReservesOutput()
    {
        var options = new LLMOptions { DefaultProvider = "deployment", DefaultModel = "known", Models = { ["deployment"] = new() { Type = "openai" } },
            ModelOverrides = { ["openai/known"] = new() { MaxInputTokens = 12000, ContextWindowTokens = 16000, MaxOutputTokens = 8192 } } };
        var adapter = new RoutingLLMClientAdapter(new RoutingLLMClient(options, []));
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(7808, await adapter.InputTokenAllowanceAsync(null, "known", 8192, ct));
        Assert.Null(await adapter.InputTokenAllowanceAsync(null, "unrecognized-model", 8192, ct));
        Assert.Null(await adapter.InputTokenAllowanceAsync(null, "gpt-4o-mni", 8192, ct));
        Assert.Null(await adapter.InputTokenAllowanceAsync(null, "known", 8193, ct));
    }
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
