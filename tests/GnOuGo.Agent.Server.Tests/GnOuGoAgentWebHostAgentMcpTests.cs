using Microsoft.Extensions.DependencyInjection;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GnOuGoAgentWebHostAgentMcpTests
{
    [Fact]
    public async Task Build_StartsMountedAgentMcpEndpoint_AndListsToolsOverHttp()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GnOuGo.Agent.Server"));
        var app = GnOuGoAgentWebHost.Build(TelemetryTestHostArgs.Create(), urls: "http://127.0.0.1:0", contentRoot: contentRoot, enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();

            var store = app.Services.GetRequiredService<LLMRuntimeOptionsStore>();
            McpServerOptions? agentMcp = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                store.Current.McpServers.TryGetValue("GnOuGo.Agent.Mcp", out agentMcp);
                if (!string.IsNullOrWhiteSpace(agentMcp?.Url) && !agentMcp.Url.Contains(":0/", StringComparison.Ordinal))
                    break;

                await Task.Delay(50);
            }

            Assert.NotNull(agentMcp);
            Assert.Equal("http", agentMcp!.Type);
            Assert.Contains("/mcp/agent", agentMcp.Url, StringComparison.Ordinal);
            Assert.DoesNotContain(":0/", agentMcp.Url, StringComparison.Ordinal);

            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["GnOuGo.Agent.Mcp"] = agentMcp
            });

            await using var session = await factory.GetClientAsync("GnOuGo.Agent.Mcp", CancellationToken.None);
            var tools = await session.ListToolsAsync(CancellationToken.None);

            Assert.Contains(tools, tool => tool.Name == "agent_list");
            Assert.Contains(tools, tool => tool.Name == "user_chat_history_append");
            Assert.Contains(tools, tool => tool.Name == "user_config_get");
            Assert.Contains(tools, tool => tool.Name == "user_config_set");

            var setResult = await session.CallToolAsync("user_config_set", new System.Text.Json.Nodes.JsonObject
            {
                ["defaultLlmProvider"] = "ollama",
                ["defaultLlmModel"] = "llama3:8b",
                ["defaultAgent"] = "slimfaas"
            }, CancellationToken.None);

            Assert.False(setResult.IsError);

            var getResult = await session.CallToolAsync("user_config_get", null, CancellationToken.None);
            Assert.False(getResult.IsError);

            var payload = Assert.IsType<System.Text.Json.Nodes.JsonObject>(getResult.Content);
            Assert.True(payload["success"]!.GetValue<bool>());
            var config = Assert.IsType<System.Text.Json.Nodes.JsonObject>(payload["config"]);
            Assert.Equal("ollama", config["default_llm_provider"]!.GetValue<string>());
            Assert.Equal("llama3:8b", config["default_llm_model"]!.GetValue<string>());
            Assert.Equal("slimfaas", config["default_agent"]!.GetValue<string>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

