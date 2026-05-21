using Microsoft.Extensions.DependencyInjection;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GnOuGoAgentWebHostAgentMcpTests
{
    private static string GetServerContentRoot()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src",
            "GnOuGo.Agent.Server"));

        Assert.True(Directory.Exists(contentRoot), $"Content root not found: {contentRoot}");
        return contentRoot;
    }

    [Fact]
    public async Task Build_StartsMountedAgentMcpEndpoint_AndPublishesStableRuntimeUrl()
    {
        var contentRoot = GetServerContentRoot();
        var app = GnOuGoAgentWebHost.Build(TelemetryTestHostArgs.Create(), urls: "http://127.0.0.1:0", contentRoot: contentRoot, enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);

            var agentMcp = await WaitForMountedAgentMcpAsync(app.Services, CancellationToken.None);

            Assert.NotNull(agentMcp);
            Assert.Equal("http", agentMcp!.Type);
            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/agent", agentMcp.Url);
            Assert.DoesNotContain(":0/", agentMcp.Url, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Build_StartsMountedAgentMcpEndpoint_AgentUserConfigClientCanReadSnapshot()
    {
        var contentRoot = GetServerContentRoot();
        var app = GnOuGoAgentWebHost.Build(TelemetryTestHostArgs.Create(), urls: "http://127.0.0.1:0", contentRoot: contentRoot, enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();
            await WaitForMountedAgentMcpAsync(app.Services, CancellationToken.None);

            var client = app.Services.GetRequiredService<AgentUserConfigMcpClient>();
            var snapshot = await client.GetAsync(CancellationToken.None);

            Assert.NotNull(snapshot);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Build_StartsMountedAgentMcpEndpoint_AndListsToolsOverHttp_WhenOptInEnabled()
    {
        if (!AgentServerTestEnvironment.RunMountedAgentMcpTests)
            return;

        var contentRoot = GetServerContentRoot();
        var app = GnOuGoAgentWebHost.Build(TelemetryTestHostArgs.Create(), urls: "http://127.0.0.1:0", contentRoot: contentRoot, enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);

            var agentMcp = await WaitForMountedAgentMcpAsync(app.Services, CancellationToken.None);

            Assert.NotNull(agentMcp);
            Assert.Equal("http", agentMcp!.Type);
            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/agent", agentMcp.Url);
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

    private static async Task<McpServerOptions?> WaitForMountedAgentMcpAsync(IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetRequiredService<LLMRuntimeOptionsStore>();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            store.Current.McpServers.TryGetValue("GnOuGo.Agent.Mcp", out var agentMcp);
            if (!string.IsNullOrWhiteSpace(agentMcp?.Url) && !agentMcp.Url.Contains(":0/", StringComparison.Ordinal))
                return agentMcp;

            await Task.Delay(50, ct);
        }

        store.Current.McpServers.TryGetValue("GnOuGo.Agent.Mcp", out var finalAgentMcp);
        return finalAgentMcp;
    }
}

