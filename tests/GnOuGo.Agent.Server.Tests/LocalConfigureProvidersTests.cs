using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LocalConfigureProvidersTests
{
    [Fact]
    public async Task LlmList_IncludesBuiltInLocalCatalogAndInstallState()
    {
        var options = CreateOptions(defaultProvider: "OpenAi");
        var service = SmartFlowTestFactory.CreateProvidersService(
            new RecordingLlmClient(),
            options: options,
            localModels: new InstalledModelManager());

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/llm list", CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.Contains("Local", events.Single().Text, StringComparison.Ordinal);
        Assert.Contains("qwen3:0.6b", events.Single().Text, StringComparison.Ordinal);
        Assert.Contains("(built-in)", events.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmDefaultLocal_SelectsInstalledModelWithoutCallingLlm()
    {
        var options = CreateOptions(defaultProvider: "OpenAi");
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        var llm = new RecordingLlmClient();
        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            options: options,
            runtimeOptionsStore: store,
            localModels: new InstalledModelManager());

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/llm default local qwen3:0.6b", CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, llm.CallCount);
        Assert.Equal("Local", store.Current.DefaultProvider);
        Assert.Equal("qwen3:0.6b", store.Current.DefaultModel);
        Assert.Contains("Default LLM provider set", events.Single().Text, StringComparison.Ordinal);
    }

    private static LLMOptions CreateOptions(string defaultProvider)
        => new()
        {
            DefaultProvider = defaultProvider,
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenAi"] = new() { Type = "openai", Url = "https://api.openai.com/v1" },
                ["Local"] = new() { Type = "local", Url = "embedded://llamasharp" }
            }
        };

    private sealed class InstalledModelManager : ILocalModelManager
    {
        private static readonly LocalModelInfo Installed = new(
            "qwen3:0.6b",
            "Qwen3 0.6B Q4_0",
            LocalModelStatus.Installed,
            428970080,
            428970080,
            "Apache-2.0",
            "pinned-test-source");

        public Task<LocalModelInfo> InstallAsync(
            string modelId,
            IProgress<LocalModelProgress>? progress = null,
            CancellationToken ct = default)
            => Task.FromResult(Installed);

        public Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocalModelInfo>>([Installed]);

        public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
