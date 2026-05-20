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
    [InlineData("claude", "claude")]
    [InlineData("anthropic", "claude")]
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
    [InlineData("https://api.anthropic.com/v1", "claude")]
    [InlineData("https://claude-proxy.example.com/v1", "claude")]
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
}

