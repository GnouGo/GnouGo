using GnOuGo.Agent.Server.Hosting;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LocalFallbackValidationTests
{
    [Fact]
    public void ValidateLocalFallback_AcceptsConfiguredNonLocalProvider()
    {
        var options = Options();
        options.Fallback = new LLMFallbackOptions { Provider = "Cloud", Model = "cloud-model" };

        GnOuGoAgentWebHost.ValidateLocalFallback(options);
    }

    [Fact]
    public void ValidateLocalFallback_RejectsIncompleteConfiguration()
    {
        var options = Options();
        options.Fallback = new LLMFallbackOptions { Provider = "Cloud", Model = "" };

        var error = Assert.Throws<InvalidOperationException>(() => GnOuGoAgentWebHost.ValidateLocalFallback(options));

        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateLocalFallback_RejectsMissingAndSelfReferencingProviders()
    {
        var missing = Options();
        missing.Fallback = new LLMFallbackOptions { Provider = "Missing", Model = "model" };
        Assert.Throws<InvalidOperationException>(() => GnOuGoAgentWebHost.ValidateLocalFallback(missing));

        var local = Options();
        local.Fallback = new LLMFallbackOptions { Provider = "Local", Model = "qwen3:0.6b" };
        Assert.Throws<InvalidOperationException>(() => GnOuGoAgentWebHost.ValidateLocalFallback(local));
    }

    [Fact]
    public void ValidateLocalFallback_RejectsAmbiguousProviderKeys()
    {
        var options = Options();
        options.Models = new Dictionary<string, ModelProviderOptions>(StringComparer.Ordinal)
        {
            ["Cloud"] = new() { Type = "openai", Url = "https://cloud.invalid" },
            ["cloud"] = new() { Type = "openai", Url = "https://other.invalid" }
        };
        options.Fallback = new LLMFallbackOptions { Provider = "CLOUD", Model = "model" };

        Assert.Throws<InvalidOperationException>(() => GnOuGoAgentWebHost.ValidateLocalFallback(options));
    }

    private static LLMOptions Options()
        => new()
        {
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Local"] = new() { Type = "local", Url = "embedded://llamasharp" },
                ["Cloud"] = new() { Type = "openai", Url = "https://cloud.invalid" }
            }
        };
}
