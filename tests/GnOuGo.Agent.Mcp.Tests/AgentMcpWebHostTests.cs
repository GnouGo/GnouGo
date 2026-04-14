using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class AgentMcpWebHostTests
{
    [Fact]
    public void ResolveDatabasePath_WhenUsingDefaultPath_UsesDesktopGnOuGoDirectory()
    {
        var expected = Path.Combine(ResolveDesktopDirectory(), "GnOuGo", "data", "gnougo-agent.db");
        var actual = AgentMcpHostingExtensions.ResolveDatabasePath(AgentMcpHostingExtensions.DefaultDatabasePath, AppContext.BaseDirectory);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Build_StartsHttpHost_AndExposesHealthEndpoint()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-mcp-{Guid.NewGuid():N}.db");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();

            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            using var http = new HttpClient();
            var payload = await http.GetFromJsonAsync<HealthPayload>($"{address.TrimEnd('/')}/health");

            Assert.NotNull(payload);
            Assert.Equal("ok", payload.Status);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a temporary SQLite file.
            }
        }
    }

    [Fact]
    public async Task Build_StartsHttpHost_AndSupportsUserConfigRoundTripOverMcpHttp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-mcp-roundtrip-{Guid.NewGuid():N}.db");
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}"
        ], urls: "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();

            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["GnOuGo.Agent.Mcp"] = new()
                {
                    Type = "http",
                    Url = $"{address.TrimEnd('/')}/mcp",
                    Description = "Standalone Agent MCP test host"
                }
            });

            await using var session = await factory.GetClientAsync("GnOuGo.Agent.Mcp", CancellationToken.None);
            var tools = await session.ListToolsAsync(CancellationToken.None);

            Assert.Contains(tools, tool => tool.Name == "agent_list");
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

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a temporary SQLite file.
            }
        }
    }

    private sealed record HealthPayload(string Status);

    private static string ResolveDesktopDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
            return Path.GetFullPath(desktopPath);

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            return Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(Path.Combine(homePath, "Desktop"));

        throw new InvalidOperationException("Unable to resolve the current user's Desktop directory.");
    }
}


