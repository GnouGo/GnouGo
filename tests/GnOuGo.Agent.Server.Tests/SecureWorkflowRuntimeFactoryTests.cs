using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SecureWorkflowRuntimeFactoryTests
{
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
            existingSession.Options.McpServers["GnOuGo.GithubCopilot.Mcp"].EnvironmentVariables?["Code__Copilot__Model"]);
        Assert.Equal(
            "next-workflow-model",
            nextSession.Options.McpServers["GnOuGo.GithubCopilot.Mcp"].EnvironmentVariables?["Code__Copilot__Model"]);
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
                        ["Code__Copilot__Model"] = fallbackModel
                    }
                }
            }
        };
}
