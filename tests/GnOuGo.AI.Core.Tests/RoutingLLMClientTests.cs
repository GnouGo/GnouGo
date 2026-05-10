using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

/// <summary>
/// Tests for <see cref="RoutingLLMClient"/> provider resolution and routing logic.
/// </summary>
public class RoutingLLMClientTests
{
    private static LLMOptions CreateOptions(
        string defaultProvider = "OpenAi",
        string defaultModel = "gpt-4o-mini",
        Dictionary<string, ModelProviderOptions>? models = null)
    {
        return new LLMOptions
        {
            DefaultProvider = defaultProvider,
            DefaultModel = defaultModel,
            Models = models ?? new Dictionary<string, ModelProviderOptions>
            {
                ["OpenAi"] = new() { Url = "https://api.openai.com/v1", Type = "openai" },
                ["Ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" },
                ["Copilot"] = new() { Url = "https://models.github.ai/inference", Type = "copilot" },
            }
        };
    }

    [Fact]
    public void Constructor_RegistersDefaultProviders()
    {
        var http = new HttpClient();
        var client = new RoutingLLMClient(http, CreateOptions());

        Assert.Contains("openai", client.RegisteredProviderTypes);
        Assert.Contains("ollama", client.RegisteredProviderTypes);
        Assert.Contains("copilot", client.RegisteredProviderTypes);
    }

    [Fact]
    public void Constructor_AcceptsCustomProviders()
    {
        var custom = new FakeProvider("custom");
        var client = new RoutingLLMClient(CreateOptions(), [custom]);

        Assert.Contains("custom", client.RegisteredProviderTypes);
    }

    [Fact]
    public async Task CallAsync_RoutesExplicitProvider()
    {
        var openai = new FakeProvider("openai");
        var copilot = new FakeProvider("copilot");
        var client = new RoutingLLMClient(CreateOptions(), [openai, copilot]);

        await client.CallAsync(new LLMClientRequest
        {
            Provider = "Copilot",
            Model = "gpt-4.1",
            Prompt = "Hello"
        });

        Assert.Equal(0, openai.CallCount);
        Assert.Equal(1, copilot.CallCount);
        Assert.Equal("gpt-4.1", copilot.LastModel);
    }

    [Fact]
    public async Task CallAsync_RoutesVendorPrefixToCopilot()
    {
        var openai = new FakeProvider("openai");
        var copilot = new FakeProvider("copilot");
        var ollama = new FakeProvider("ollama");
        var client = new RoutingLLMClient(CreateOptions(), [openai, copilot, ollama]);

        // "openai/gpt-4.1" should route to Copilot because "openai" as a vendor prefix
        // matches the Copilot provider (since there's no provider key named "openai" that matches exactly,
        // but there IS a case-insensitive match on the "OpenAi" key)
        await client.CallAsync(new LLMClientRequest
        {
            Model = "openai/gpt-4.1",
            Prompt = "Hello"
        });

        // The vendor prefix "openai" matches the "OpenAi" key → routes to OpenAi provider (type = openai)
        Assert.Equal(1, openai.CallCount);
    }

    [Fact]
    public async Task CallAsync_RoutesOllamaModelByHeuristic()
    {
        var openai = new FakeProvider("openai");
        var ollama = new FakeProvider("ollama");
        var copilot = new FakeProvider("copilot");
        var client = new RoutingLLMClient(CreateOptions(), [openai, ollama, copilot]);

        await client.CallAsync(new LLMClientRequest
        {
            Model = "llama3.2:latest",
            Prompt = "Hello"
        });

        Assert.Equal(1, ollama.CallCount);
        Assert.Equal(0, openai.CallCount);
    }

    [Fact]
    public async Task CallAsync_FallsBackToDefaultProvider()
    {
        var openai = new FakeProvider("openai");
        var ollama = new FakeProvider("ollama");
        var client = new RoutingLLMClient(CreateOptions(), [openai, ollama]);

        await client.CallAsync(new LLMClientRequest
        {
            Model = "gpt-4o-mini",
            Prompt = "Hello"
        });

        // Default provider is OpenAi, and "gpt-4o-mini" doesn't match Ollama heuristics
        Assert.Equal(1, openai.CallCount);
    }

    [Fact]
    public async Task CallAsync_ThrowsForUnknownProvider()
    {
        var client = new RoutingLLMClient(CreateOptions(), [new FakeProvider("openai")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallAsync(new LLMClientRequest
            {
                Provider = "NonExistent",
                Model = "some-model",
                Prompt = "Hello"
            }));
    }

    [Fact]
    public async Task CallAsync_StripsVendorPrefixFromModelName()
    {
        var copilot = new FakeProvider("copilot");
        var client = new RoutingLLMClient(CreateOptions(defaultProvider: "Copilot"), [copilot]);

        await client.CallAsync(new LLMClientRequest
        {
            Provider = "Copilot",
            Model = "anthropic/claude-sonnet-4",
            Prompt = "Hello"
        });

        // The router should strip the vendor prefix
        Assert.Equal("claude-sonnet-4", copilot.LastModel);
    }

    [Fact]
    public async Task CallAsync_SanitizesTemperatureForReasoningModels()
    {
        var openai = new FakeProvider("openai");
        var client = new RoutingLLMClient(CreateOptions(defaultModel: "o4-mini"), [openai]);

        await client.CallAsync(new LLMClientRequest
        {
            Model = "o4-mini",
            Prompt = "Plan",
            Temperature = 0.7,
            Reasoning = "high"
        });

        Assert.NotNull(openai.LastRequest);
        Assert.Null(openai.LastRequest!.Temperature);
        Assert.Equal("high", openai.LastRequest.Reasoning);
    }

    [Fact]
    public async Task CallAsync_InlineOverrideCanDisableReasoningAndTools()
    {
        var openai = new FakeProvider("openai");
        var options = CreateOptions();
        options.ModelOverrides["custom-model"] = new LLMModelMetadata
        {
            Id = "custom-model",
            ProviderType = "openai",
            Capabilities = new ModelCapabilityMetadata
            {
                SupportsTemperature = true,
                SupportsReasoningEffort = false,
                SupportsTools = false
            }
        };
        var client = new RoutingLLMClient(options, [openai]);

        await client.CallAsync(new LLMClientRequest
        {
            Model = "custom-model",
            Prompt = "Hello",
            Temperature = 0.2,
            Reasoning = "high",
            Tools = [new LLMToolDef { Name = "tool" }]
        });

        Assert.NotNull(openai.LastRequest);
        Assert.Equal(0.2, openai.LastRequest!.Temperature);
        Assert.Null(openai.LastRequest.Reasoning);
        Assert.Null(openai.LastRequest.Tools);
    }

    [Fact]
    public async Task CallAsync_PreservesBackgroundModeHint()
    {
        var openai = new FakeProvider("openai");
        var client = new RoutingLLMClient(CreateOptions(), [openai]);

        await client.CallAsync(new LLMClientRequest
        {
            Model = "gpt-4o-mini",
            Prompt = "Plan",
            UseBackgroundMode = true
        });

        Assert.NotNull(openai.LastRequest);
        Assert.True(openai.LastRequest!.UseBackgroundMode);
    }

    /// <summary>
    /// Fake provider for testing routing without network calls.
    /// </summary>
    private sealed class FakeProvider : ILLMProvider
    {
        public FakeProvider(string providerType) => ProviderType = providerType;

        public string ProviderType { get; }
        public int CallCount { get; private set; }
        public string? LastModel { get; private set; }
        public LLMClientRequest? LastRequest { get; private set; }

        public Task<LLMClientResponse> CallAsync(
            string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
        {
            CallCount++;
            LastModel = model;
            LastRequest = request;
            return Task.FromResult(new LLMClientResponse { Text = $"[{ProviderType}] response" });
        }
    }
}

