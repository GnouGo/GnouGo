using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmRuntimeOptionsStoreTests
{
    [Fact]
    public void UpdateProvider_WhenTransientMountedMcpServersExist_KeepsEverythingInMemoryOnly()
    {
        var userSettingsPath = Path.Combine(Path.GetTempPath(), "gnougo-agent-server-tests", "runtime-settings-regression", $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userSettingsPath)!);

        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                },
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["github"] = new() { Type = "http", Url = "https://api.githubcopilot.com/mcp/", Description = "GitHub" }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.UpsertTransientMcpServer("GnOuGo.Agent.Mcp", new McpServerOptions
        {
            Type = "http",
            Url = "http://127.0.0.1:64000/mcp/agent",
            Description = "Mounted Agent MCP"
        });

        var updated = store.SetDefaultProvider("openai", "gpt-4o-mini");
        Assert.True(updated);
        Assert.False(File.Exists(userSettingsPath));

        Assert.True(store.Current.McpServers.ContainsKey("github"));
        Assert.True(store.Current.McpServers.ContainsKey("GnOuGo.Agent.Mcp"));
    }

    [Fact]
    public void ReplaceRuntimeOptions_OverwritesTheLiveSnapshot()
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai" }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.ReplaceRuntimeOptions(new LLMOptions
        {
            DefaultProvider = "ollama",
            DefaultModel = "llama3.2",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
            },
            McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Github"] = new() { Type = "http", Url = "https://api.githubcopilot.com/mcp/" }
            }
        });

        Assert.Equal("ollama", store.Current.DefaultProvider, ignoreCase: true);
        Assert.Equal("llama3.2", store.Current.DefaultModel);
        Assert.True(store.Current.Models.ContainsKey("ollama"));
        Assert.True(store.Current.McpServers.ContainsKey("Github"));
    }
}


