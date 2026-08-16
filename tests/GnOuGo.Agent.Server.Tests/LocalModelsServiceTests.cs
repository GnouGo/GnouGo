using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.AI.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LocalModelsServiceTests
{
    [Fact]
    public async Task Install_ActivatesLocalWhenOpenAiPlaceholderHasNoAuthentication()
    {
        using var environment = new EnvironmentVariableScope("OPENAI_API_KEY", null);
        var manager = new FakeLocalModelManager();
        var store = CreateStore("OpenAi", "gpt-4o-mini", new ModelProviderOptions
        {
            Type = "openai",
            Url = "https://api.openai.com/v1",
            ApiKey = ""
        });
        var service = CreateService(manager, store);

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/models install qwen3:0.6b", CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.Equal("Local", store.Current.DefaultProvider);
        Assert.Equal("qwen3:0.6b", store.Current.DefaultModel);
        Assert.Contains("now the active default", events[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_PreservesUsableOllamaDefault()
    {
        var manager = new FakeLocalModelManager();
        var store = CreateStore("Ollama", "llama3", new ModelProviderOptions
        {
            Type = "ollama",
            Url = "http://localhost:11434"
        });
        var service = CreateService(manager, store);

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/models install qwen3:0.6b", CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.Equal("Ollama", store.Current.DefaultProvider);
        Assert.Equal("llama3", store.Current.DefaultModel);
        Assert.Contains("existing usable default was preserved", events[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_RejectsActiveModelWithoutForce()
    {
        var manager = new FakeLocalModelManager();
        var store = CreateStore("Local", "qwen3:0.6b", new ModelProviderOptions
        {
            Type = "local",
            Url = "embedded://llamasharp"
        });
        var service = CreateService(manager, store);

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/models remove qwen3:0.6b", CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, manager.RemoveCalls);
        Assert.Contains("--force", events.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ForceUnsetsLocalDefaultAndRemovesModel()
    {
        var options = new LLMOptions
        {
            DefaultProvider = "Local",
            DefaultModel = "qwen3:0.6b",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Local"] = new() { Type = "local", Url = "embedded://llamasharp" },
                ["Ollama"] = new() { Type = "ollama", Url = "http://localhost:11434" }
            }
        };
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        var manager = new FakeLocalModelManager();
        var service = CreateService(manager, store);

        var events = await SmartFlowTestFactory.CollectAsync(
            service.ExecuteAsync("/models remove qwen3:0.6b --force", CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, manager.RemoveCalls);
        Assert.Equal("Ollama", store.Current.DefaultProvider);
        Assert.Contains("Removed", events.Single().Text, StringComparison.Ordinal);
    }

    private static LLMRuntimeOptionsStore CreateStore(
        string defaultProvider,
        string defaultModel,
        ModelProviderOptions provider)
    {
        var options = new LLMOptions
        {
            DefaultProvider = defaultProvider,
            DefaultModel = defaultModel,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [defaultProvider] = provider,
                ["Local"] = new() { Type = "local", Url = "embedded://llamasharp" }
            }
        };
        return SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
    }

    private static LocalModelsService CreateService(
        ILocalModelManager manager,
        LLMRuntimeOptionsStore store)
        => new(manager, store, NullLogger<LocalModelsService>.Instance);

    private sealed class FakeLocalModelManager : ILocalModelManager
    {
        private readonly LocalModelInfo _model = new(
            "qwen3:0.6b",
            "Qwen3 0.6B Q4_0",
            LocalModelStatus.Installed,
            428970080,
            428970080,
            "Apache-2.0",
            "pinned-test-source");

        public int RemoveCalls { get; private set; }

        public Task<LocalModelInfo> InstallAsync(
            string modelId,
            IProgress<LocalModelProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new LocalModelProgress(modelId, _model.TotalBytes, _model.TotalBytes, 100));
            return Task.FromResult(_model);
        }

        public Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LocalModelInfo>>([_model]);
        }

        public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemoveCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
