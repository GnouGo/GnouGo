using Microsoft.Extensions.DependencyInjection;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class AgentUserConfigHydrationTests
{
    [Fact]
    public async Task Build_LoadsPersistedDefaultLlmFromMountedAgentMcp_AfterStartup()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-config-{Guid.NewGuid():N}.db");

        await AgentMcpTestPersistence.SeedUserConfigAsync(dbPath, new UserConfigUpdate("ollama", "llama3:8b", "slimfaas"), TestContext.Current.CancellationToken);

        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GnOuGo.Agent.Server"));
        var app = GnOuGoAgentWebHost.Build(
            TelemetryTestHostArgs.Create(
                $"--Agent:DatabasePath={dbPath}",
                "--LLM:DefaultProvider=OpenAi",
                "--LLM:DefaultModel=gpt-4o-mini",
                "--LLM:Models:OpenAi:Url=https://api.openai.com/v1",
                "--LLM:Models:OpenAi:Type=openai",
                "--LLM:Models:Ollama:Url=http://localhost:11434",
                "--LLM:Models:Ollama:Type=ollama"),
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);

            var store = app.Services.GetRequiredService<LLMRuntimeOptionsStore>();
            await TestPolling.WaitUntilAsync(
                () => string.Equals(store.Current.DefaultProvider, "Ollama", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(store.Current.DefaultModel, "llama3:8b", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);

            Assert.Equal("ollama", store.Current.DefaultProvider, ignoreCase: true);
            Assert.Contains(store.Current.DefaultModel, new[] { "llama3:8b", "llama3" });
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
            }
        }
    }
}



