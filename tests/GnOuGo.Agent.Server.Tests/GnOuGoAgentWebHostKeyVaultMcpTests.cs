using Microsoft.Extensions.DependencyInjection;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GnOuGoAgentWebHostKeyVaultMcpTests
{
    [Fact]
    public async Task Build_StartsMountedKeyVaultMcpEndpoint_AndListsToolsOverHttp()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GnOuGo.Agent.Server"));
        var app = GnOuGoAgentWebHost.Build(TelemetryTestHostArgs.Create(), urls: "http://127.0.0.1:0", contentRoot: contentRoot, enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);

            var store = app.Services.GetRequiredService<LLMRuntimeOptionsStore>();
            McpServerOptions? keyVault = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                store.Current.McpServers.TryGetValue("GnOuGo.KeyVault.Mcp", out keyVault);
                if (!string.IsNullOrWhiteSpace(keyVault?.Url) && !keyVault.Url.Contains(":0/", StringComparison.Ordinal))
                    break;

                await Task.Delay(50);
            }

            Assert.NotNull(keyVault);
            Assert.Equal("http", keyVault!.Type);
            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/keyvault", keyVault.Url);
            Assert.DoesNotContain(":0/", keyVault.Url, StringComparison.Ordinal);

            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["GnOuGo.KeyVault.Mcp"] = keyVault
            });

            await using var session = await factory.GetClientAsync("GnOuGo.KeyVault.Mcp", CancellationToken.None);
            var tools = await session.ListToolsAsync(CancellationToken.None);

            Assert.Contains(tools, tool => tool.Name == "keyvault_list_tenants");
            Assert.Contains(tools, tool => tool.Name == "keyvault_set_secret");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}




