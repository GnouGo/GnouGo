using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

/// <summary>
/// Tests for <see cref="ModelProviderOptions.ResolvedType"/> including Copilot detection.
/// </summary>
public class ModelProviderOptionsTests
{
    [Theory]
    [InlineData("openai", "openai")]
    [InlineData("ollama", "ollama")]
    [InlineData("copilot", "copilot")]
    [InlineData("claude", "anthropic")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("Copilot", "copilot")]
    [InlineData("OPENAI", "openai")]
    public void ResolvedType_UsesExplicitType(string type, string expected)
    {
        var opts = new ModelProviderOptions { Url = "https://example.com", Type = type };
        Assert.Equal(expected, opts.ResolvedType);
    }

    [Theory]
    [InlineData("http://localhost:11434", "ollama")]
    [InlineData("http://my-ollama-server:11434", "ollama")]
    [InlineData("https://api.openai.com/v1", "openai")]
    [InlineData("https://api.anthropic.com/v1", "anthropic")]
    [InlineData("https://claude-proxy.example.com/v1", "anthropic")]
    [InlineData("https://models.github.ai/inference", "copilot")]
    [InlineData("https://copilot-proxy.example.com/v1", "copilot")]
    public void ResolvedType_InfersFromUrl(string url, string expected)
    {
        var opts = new ModelProviderOptions { Url = url };
        Assert.Equal(expected, opts.ResolvedType);
    }

    [Fact]
    public void ResolvedType_DefaultsToOpenAi_WhenNoHintOrUrlMatch()
    {
        var opts = new ModelProviderOptions { Url = "https://my-custom-llm.example.com/api" };
        Assert.Equal("openai", opts.ResolvedType);
    }

    [Fact]
    public void ValidateAndThrow_AcceptsStandardsBasedPolicyDefaults()
    {
        var options = new LLMOptions
        {
            Models = { ["gateway"] = new ModelProviderOptions() }
        };

        LLMOptionsValidation.ValidateAndThrow(options);
    }

    [Fact]
    public void ValidateAndThrow_RejectsConfiguredModeWithoutDefault()
    {
        var options = new LLMOptions
        {
            Models =
            {
                ["gateway"] = new ModelProviderOptions
                {
                    RequestPolicy = new LLMProviderRequestPolicyOptions
                    {
                        UnspecifiedOutputTokens = LLMUnspecifiedOutputTokensMode.Configured
                    }
                }
            }
        };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            LLMOptionsValidation.ValidateAndThrow(options));

        Assert.Contains("DefaultMaxOutputTokens", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAndThrow_RejectsDefaultAboveProviderCap()
    {
        var options = new LLMOptions
        {
            Models =
            {
                ["gateway"] = new ModelProviderOptions
                {
                    RequestPolicy = new LLMProviderRequestPolicyOptions
                    {
                        UnspecifiedOutputTokens = LLMUnspecifiedOutputTokensMode.Configured,
                        DefaultMaxOutputTokens = 8_192,
                        MaxOutputTokensCap = 4_096
                    }
                }
            }
        };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            LLMOptionsValidation.ValidateAndThrow(options));

        Assert.Contains("cannot exceed", failure.Message, StringComparison.Ordinal);
    }
}
