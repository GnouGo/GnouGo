using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GnOuGoAgentWebHostMcpIsolationTests
{
    private static readonly string[] AgentToolNames =
    [
        "agent_add",
        "agent_update",
        "agent_list",
        "agent_delete",
        "agent_get_by_name",
        "user_chat_history_append",
        "user_chat_history_get",
        "user_config_get",
        "user_config_set"
    ];

    private static readonly string[] KeyVaultToolNames =
    [
        "keyvault_list_tenants",
        "keyvault_create_tenant",
        "keyvault_set_secret",
        "keyvault_list_secrets",
        "keyvault_get_secret",
        "keyvault_delete_secret"
    ];

    private static readonly string[] DocsIngestorToolNames =
    [
        "docs_ingestor_vectorize_files",
        "docs_ingestor_ingest_files",
        "docs_ingestor_list_files",
        "docs_ingestor_vector_search",
        "docs_ingestor_download_original",
        "docs_ingestor_delete_file"
    ];

    [Fact]
    public async Task DirectEndpoints_KeepToolCatalogsRequestLocalDuringConcurrentDiscovery()
    {
        if (!AgentServerTestEnvironment.RunMountedMcpHttpTests)
            return;

        var contentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src",
            "GnOuGo.Agent.Server"));
        var app = GnOuGoAgentWebHost.Build(
            TelemetryTestHostArgs.Create(),
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);
            var servers = await WaitForDirectServersAsync(app.Services, TestContext.Current.CancellationToken);

            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/agent", servers["GnOuGo.Agent.Mcp"].Url);
            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/keyvault", servers["GnOuGo.KeyVault.Mcp"].Url);
            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/docs-ingestor", servers["GnOuGo.DocIngestor.Mcp"].Url);

            await using var factory = new ConfiguredMcpClientFactory(servers);
            await using var agent = await factory.GetClientAsync("GnOuGo.Agent.Mcp", TestContext.Current.CancellationToken);
            await using var keyVault = await factory.GetClientAsync("GnOuGo.KeyVault.Mcp", TestContext.Current.CancellationToken);
            await using var docs = await factory.GetClientAsync("GnOuGo.DocIngestor.Mcp", TestContext.Current.CancellationToken);

            var catalogs = await Task.WhenAll(
                agent.ListToolsAsync(TestContext.Current.CancellationToken),
                keyVault.ListToolsAsync(TestContext.Current.CancellationToken),
                docs.ListToolsAsync(TestContext.Current.CancellationToken));

            Assert.Equal(AgentToolNames.Order(), catalogs[0].Select(static tool => tool.Name).Order());
            Assert.Equal(KeyVaultToolNames.Order(), catalogs[1].Select(static tool => tool.Name).Order());
            Assert.Equal(DocsIngestorToolNames.Order(), catalogs[2].Select(static tool => tool.Name).Order());

            Assert.False((await agent.CallToolAsync("user_config_get", null, TestContext.Current.CancellationToken)).IsError);
            Assert.False((await keyVault.CallToolAsync("keyvault_list_tenants", null, TestContext.Current.CancellationToken)).IsError);
            Assert.False((await docs.CallToolAsync("docs_ingestor_list_files", null, TestContext.Current.CancellationToken)).IsError);
            Assert.True(await IsRejectedAsync(agent, "keyvault_list_tenants", TestContext.Current.CancellationToken));
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    private static async Task<Dictionary<string, McpServerOptions>> WaitForDirectServersAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        var store = services.GetRequiredService<LLMRuntimeOptionsStore>();
        var names = new[] { "GnOuGo.Agent.Mcp", "GnOuGo.KeyVault.Mcp", "GnOuGo.DocIngestor.Mcp" };
        var servers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase);

        await TestPolling.WaitUntilAsync(() =>
        {
            servers.Clear();
            foreach (var name in names)
            {
                if (!store.Current.McpServers.TryGetValue(name, out var server) ||
                    string.IsNullOrWhiteSpace(server.Url) ||
                    server.Url.Contains(":0/", StringComparison.Ordinal))
                {
                    return false;
                }

                servers[name] = server;
            }

            return true;
        }, ct);

        return servers;
    }

    private static async Task<bool> IsRejectedAsync(IMcpSession session, string toolName, CancellationToken ct)
    {
        try
        {
            return (await session.CallToolAsync(toolName, null, ct)).IsError;
        }
        catch
        {
            return true;
        }
    }
}
