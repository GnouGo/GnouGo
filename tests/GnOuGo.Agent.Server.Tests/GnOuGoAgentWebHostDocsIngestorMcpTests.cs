using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class GnOuGoAgentWebHostDocsIngestorMcpTests
{
    [Fact]
    public async Task Build_StartsMountedDocsIngestorMcpEndpoint_AndListsToolsOverHttp()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GnOuGo.Agent.Server"));
        var app = GnOuGoAgentWebHost.Build(TelemetryTestHostArgs.Create(), urls: "http://127.0.0.1:0", contentRoot: contentRoot, enableHttpsRedirection: false);

        try
        {
            await app.StartAsync();
            var publishedEndpoints = GnOuGoAgentWebHost.ResolvePublishedEndpoints(app);

            var store = app.Services.GetRequiredService<LLMRuntimeOptionsStore>();
            McpServerOptions? docsIngestor = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                store.Current.McpServers.TryGetValue("GnOuGo.DocsIngestor.Mcp", out docsIngestor);
                if (!string.IsNullOrWhiteSpace(docsIngestor?.Url) && !docsIngestor.Url.Contains(":0/", StringComparison.Ordinal))
                    break;

                await Task.Delay(50);
            }

            Assert.NotNull(docsIngestor);
            Assert.Equal("http", docsIngestor!.Type);
            Assert.Equal($"{publishedEndpoints.AppBaseAddress}/mcp/docs-ingestor", docsIngestor.Url);
            Assert.DoesNotContain(":0/", docsIngestor.Url, StringComparison.Ordinal);

            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["GnOuGo.DocsIngestor.Mcp"] = docsIngestor
            });

            await using var session = await factory.GetClientAsync("GnOuGo.DocsIngestor.Mcp", CancellationToken.None);
            var tools = await session.ListToolsAsync(CancellationToken.None);

            Assert.Contains(tools, tool => tool.Name == "docs_ingestor_list_files");
            Assert.Contains(tools, tool => tool.Name == "docs_ingestor_vector_search");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}


