using GnOuGo.Auth.Core;

namespace GnOuGo.AI.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotApiKeyProvider"/>.
/// </summary>
public class CopilotApiKeyProviderTests
{
    [Fact]
    public async Task GetApiKeyAsync_ReturnsConfiguredKey()
    {
        var provider = new CopilotApiKeyProvider("ghp_test123");
        var key = await provider.GetApiKeyAsync();
        Assert.Equal("ghp_test123", key);
    }

    [Fact]
    public async Task GetApiKeyAsync_FallsBackToGitHubToken()
    {
        var saved = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var saved2 = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-gh-token");
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", null);

            var provider = new CopilotApiKeyProvider(configuredKey: null);
            var key = await provider.GetApiKeyAsync();
            Assert.Equal("env-gh-token", key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", saved);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", saved2);
        }
    }

    [Fact]
    public async Task GetApiKeyAsync_FallsBackToCopilotApiKey()
    {
        var saved1 = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var saved2 = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", "cop-key-456");

            var provider = new CopilotApiKeyProvider(configuredKey: null);
            var key = await provider.GetApiKeyAsync();
            Assert.Equal("cop-key-456", key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", saved1);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", saved2);
        }
    }

    [Fact]
    public async Task GetApiKeyAsync_ThrowsWhenNothingConfigured()
    {
        var saved1 = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var saved2 = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", null);

            var provider = new CopilotApiKeyProvider(configuredKey: null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetApiKeyAsync().AsTask());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", saved1);
            Environment.SetEnvironmentVariable("COPILOT_API_KEY", saved2);
        }
    }
}

