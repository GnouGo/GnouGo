using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotLLMProvider"/> utility methods.
/// </summary>
public class CopilotLLMProviderTests
{
    [Theory]
    [InlineData("openai/gpt-4.1", "gpt-4.1")]
    [InlineData("anthropic/claude-sonnet-4", "claude-sonnet-4")]
    [InlineData("meta/llama-4-scout", "llama-4-scout")]
    [InlineData("deepseek/deepseek-r1", "deepseek-r1")]
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("o4-mini", "o4-mini")]
    [InlineData("", "")]
    public void StripVendorPrefix_HandlesKnownPatterns(string input, string expected)
    {
        var result = CopilotLLMProvider.StripVendorPrefix(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://models.github.ai/inference", "https://models.github.ai/inference/chat/completions")]
    [InlineData("https://models.github.ai/inference/", "https://models.github.ai/inference/chat/completions")]
    [InlineData("https://custom.host/v1", "https://custom.host/v1/chat/completions")]
    [InlineData("https://custom.host/chat/completions", "https://custom.host/chat/completions")]
    public void BuildChatCompletionsUrl_BuildsCorrectUrl(string baseUrl, string expected)
    {
        var result = CopilotLLMProvider.BuildChatCompletionsUrl(baseUrl);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ProviderType_IsCopilot()
    {
        var provider = new CopilotLLMProvider(new HttpClient());
        Assert.Equal("copilot", provider.ProviderType);
    }

    [Fact]
    public void ResolveToken_PrefersConfiguredApiKey()
    {
        var opts = new ModelProviderOptions { ApiKey = "test-token-123" };
        var token = CopilotLLMProvider.ResolveToken(opts);
        Assert.Equal("test-token-123", token);
    }

    [Fact]
    public void ResolveToken_ReturnsNull_WhenNothingConfigured()
    {
        // Save and clear env vars
        var saved1 = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var saved2 = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", null);

            var opts = new ModelProviderOptions { ApiKey = null };
            var token = CopilotLLMProvider.ResolveToken(opts);
            Assert.Null(token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", saved1);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", saved2);
        }
    }
}

