using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SecureWorkflowRuntimeFactoryTests
{
    [Theory]
    [InlineData("github", "pull_request_read", "pull_request_review_write")]
    [InlineData("configured-service", "inspect", "update")]
    public async Task CreateAsync_PreservesConfiguredToolsAndTheirDeclaredEffects(string server, string read, string write)
    {
        var upstream = new InMemoryMcpClientFactory();
        var configuration = new MockMcpServerConfig();
        foreach (var (name, effect) in new[] { (read, "read"), (write, "write"), ("unclassified", "unknown") })
            configuration.Tools.Add(new() { Name = name, EffectKind = effect, InputSchema = new JsonObject { ["type"] = "object" } });
        upstream.RegisterServer(server, configuration);
        var options = new LLMOptions();
        var factory = new SecureWorkflowRuntimeFactory(
            new LLMRuntimeOptionsStore(Options.Create(options), NullLogger<LLMRuntimeOptionsStore>.Instance),
            new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options), mcpClientFactoryOverride: upstream);

        await using var runtime = await factory.CreateAsync(TestContext.Current.CancellationToken);
        var planning = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = runtime.McpClientFactory }, (_, _) => Task.CompletedTask);
        var catalog = await ResolveAllAsync(planning);

        Assert.Equal([server], runtime.McpClientFactory.ServerMetadata.Select(item => item.Name));
        Assert.Equal(3, catalog.Capabilities.Count);
        Assert.All(catalog.Capabilities, capability => Assert.Equal(server, capability.Server));
        foreach (var tool in configuration.Tools)
            Assert.Equal(tool.EffectKind, catalog.Capabilities.Single(capability => capability.Method == tool.Name).EffectKind);
    }

    [Fact]
    public async Task RemovedVirtualToolsFailNormalCatalogRevalidation()
    {
        var oldFactory = new InMemoryMcpClientFactory();
        oldFactory.RegisterServer("GnOuGo.Review", new()
        {
            Tools = [new() { Name = "review_evaluate", EffectKind = "none" }, new() { Name = "review_publish", EffectKind = "write" }]
        });
        var previous = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = oldFactory }, (_, _) => Task.CompletedTask);
        var savedCatalog = await ResolveAllAsync(previous);
        var options = new LLMOptions();
        var factory = new SecureWorkflowRuntimeFactory(
            new LLMRuntimeOptionsStore(Options.Create(options), NullLogger<LLMRuntimeOptionsStore>.Instance),
            new FakeKeyVaultRuntimeConfigStore().WithEffectiveOptions(options));
        await using var runtime = await factory.CreateAsync(TestContext.Current.CancellationToken);
        var current = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = runtime.McpClientFactory }, (_, _) => Task.CompletedTask);

        var diagnostics = await current.ValidateCatalogAsync(savedCatalog, TestContext.Current.CancellationToken);

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Equal("CATALOG_CHANGED", diagnostic.Code));
        Assert.Equal(savedCatalog.Capabilities.Select(capability => capability.Id), diagnostics.Select(diagnostic => diagnostic.Location));
        Assert.Equal(2, savedCatalog.Capabilities.Count);
    }

    [Fact]
    public async Task CreateAsync_CapturesCurrentOverridesWithoutMutatingAnExistingWorkflowSession()
    {
        var baseOptions = CopilotOptions("packaged-model");
        var optionsStore = new LLMRuntimeOptionsStore(
            Options.Create(baseOptions),
            NullLogger<LLMRuntimeOptionsStore>.Instance);
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .WithEffectiveOptions(CopilotOptions("first-workflow-model"));
        var factory = new SecureWorkflowRuntimeFactory(optionsStore, keyVaultStore);

        await using var existingSession = await factory.CreateAsync(TestContext.Current.CancellationToken);
        keyVaultStore.WithEffectiveOptions(CopilotOptions("next-workflow-model"));
        await using var nextSession = await factory.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "first-workflow-model",
            existingSession.Options.McpServers["GnOuGo.GithubCopilot.Mcp"].EnvironmentVariables?["TEST_WORKFLOW_VALUE"]);
        Assert.Equal(
            "next-workflow-model",
            nextSession.Options.McpServers["GnOuGo.GithubCopilot.Mcp"].EnvironmentVariables?["TEST_WORKFLOW_VALUE"]);
    }

    private static async Task<PlanningCatalog> ResolveAllAsync(IPlanningRuntime runtime)
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = await runtime.DiscoverAsync(new(), ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(ct))
            foreach (var capability in (await runtime.Capabilities.ListAsync(source.Id, null, ct)).Capabilities)
                catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(capability, ct));
        return catalog;
    }

    private static LLMOptions CopilotOptions(string fallbackModel)
        => new()
        {
            McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["GnOuGo.GithubCopilot.Mcp"] = new()
                {
                    Type = "stdio",
                    Command = "tools/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp",
                    Args = [],
                    EnvironmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TEST_WORKFLOW_VALUE"] = fallbackModel
                    }
                }
            }
        };
}
