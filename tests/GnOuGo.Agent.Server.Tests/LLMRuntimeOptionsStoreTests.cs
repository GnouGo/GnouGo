using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmRuntimeOptionsStoreTests
{
    [Fact]
    public void Persist_WhenTransientMountedMcpServersExist_DoesNotWriteThemToUserSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "gnougo-agent-server-tests", "runtime-settings-regression", $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

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
            NullLogger<LLMRuntimeOptionsStore>.Instance,
            settingsPath);

        store.UpsertTransientMcpServer("GnOuGo.Agent.Mcp", new McpServerOptions
        {
            Type = "http",
            Url = "http://127.0.0.1:64000/mcp/agent",
            Description = "Mounted Agent MCP"
        });

        var updated = store.SetDefaultProvider("openai", "gpt-4o-mini");
        Assert.True(updated);
        Assert.True(File.Exists(settingsPath));

        var json = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
        Assert.NotNull(json);

        var llm = Assert.IsType<JsonObject>(json["LLM"]);
        var mcpServers = Assert.IsType<JsonObject>(llm["McpServers"]);
        Assert.True(mcpServers.ContainsKey("github"));
        Assert.False(mcpServers.ContainsKey("GnOuGo.Agent.Mcp"));
    }
}


