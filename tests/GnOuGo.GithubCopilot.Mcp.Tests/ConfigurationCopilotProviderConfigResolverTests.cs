using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class ConfigurationCopilotProviderConfigResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenProviderIsOmitted()
    {
        using var services = CreateServices();
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync(null, "fallback-model", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_UsesHostDefaultProviderMetadataWhenProviderIsOmitted()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["GNouGo:DefaultLlmProvider"] = "OpenAi",
            ["GNouGo:DefaultLlmModel"] = "host-default-model",
            ["Code:Copilot:Providers:OpenAi:url"] = "https://api.openai.com/v1",
            ["Code:Copilot:Providers:OpenAi:type"] = "openai",
            ["Code:Copilot:Providers:OpenAi:authType"] = "api_key",
            ["Code:Copilot:Providers:OpenAi:apiKey"] = "test-secret"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync(null, "copilot-fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("OpenAi", result.ProviderName);
        Assert.Equal("copilot-fallback-model", result.Model);
        Assert.Equal("copilot-fallback-model", result.Provider.ModelId);
        Assert.Equal("test-secret", result.Provider.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_UsesHostDefaultWhenCallerEchoesBuiltInPolicyProvider()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["GNouGo:DefaultLlmProvider"] = "OpenAi",
            ["GNouGo:DefaultLlmModel"] = "host-default-model",
            ["Code:Copilot:Providers:OpenAi:url"] = "https://api.openai.com/v1",
            ["Code:Copilot:Providers:OpenAi:type"] = "openai",
            ["Code:Copilot:Providers:OpenAi:authType"] = "api_key",
            ["Code:Copilot:Providers:OpenAi:apiKey"] = "test-secret"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("Copilot", "copilot-policy-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("OpenAi", result.ProviderName);
        Assert.Equal("copilot-policy-model", result.Model);
        Assert.Equal("test-secret", result.Provider.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_LoadsConfiguredProviderSection()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Code:Copilot:Providers:CustomCopilot:provider"] = "CustomCopilot",
            ["Code:Copilot:Providers:CustomCopilot:url"] = "https://models.github.ai/inference",
            ["Code:Copilot:Providers:CustomCopilot:type"] = "copilot",
            ["Code:Copilot:Providers:CustomCopilot:model"] = "gpt-5.4-mini",
            ["Code:Copilot:Providers:CustomCopilot:authType"] = "api_key",
            ["Code:Copilot:Providers:CustomCopilot:apiKey"] = "ghp_secret"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("CustomCopilot", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("CustomCopilot", result.ProviderName);
        Assert.Equal("gpt-5.4-mini", result.Model);
        Assert.Equal("openai", result.Provider.Type);
        Assert.Equal("completions", result.Provider.WireApi);
        Assert.Equal("https://models.github.ai/inference", result.Provider.BaseUrl);
        Assert.Equal("gpt-5.4-mini", result.Provider.ModelId);
        Assert.Equal("gpt-5.4-mini", result.Provider.WireModel);
        Assert.Null(result.Provider.ApiKey);
        Assert.Equal("ghp_secret", result.Provider.BearerToken);
    }

    [Fact]
    public async Task ResolveAsync_UsesLegacyProviderSectionName()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Code:Copilot:Providers:gnougo_llm_LegacyProvider:url"] = "https://api.openai.com/v1",
            ["Code:Copilot:Providers:gnougo_llm_LegacyProvider:type"] = "openai",
            ["Code:Copilot:Providers:gnougo_llm_LegacyProvider:model"] = "gpt-4.1",
            ["Code:Copilot:Providers:gnougo_llm_LegacyProvider:authType"] = "api_key",
            ["Code:Copilot:Providers:gnougo_llm_LegacyProvider:apiKey"] = "sk-secret"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("LegacyProvider", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("gpt-4.1", result.Model);
        Assert.Equal("sk-secret", result.Provider.ApiKey);
        Assert.Null(result.Provider.BearerToken);
    }

    [Fact]
    public async Task ResolveAsync_MapsAnthropicProviderToAnthropicMessagesWireApi()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Code:Copilot:Providers:anthropic:provider"] = "anthropic",
            ["Code:Copilot:Providers:anthropic:type"] = "anthropic",
            ["Code:Copilot:Providers:anthropic:url"] = "https://api.anthropic.com/v1",
            ["Code:Copilot:Providers:anthropic:model"] = "claude-sonnet-4-20250514",
            ["Code:Copilot:Providers:anthropic:authType"] = "api_key",
            ["Code:Copilot:Providers:anthropic:apiKey"] = "sk-ant-secret"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("anthropic", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("anthropic", result.ProviderName);
        Assert.Equal("claude-sonnet-4-20250514", result.Model);
        Assert.Equal("anthropic", result.Provider.Type);
        Assert.Equal("messages", result.Provider.WireApi);
        Assert.Equal("https://api.anthropic.com/v1", result.Provider.BaseUrl);
        Assert.Equal("claude-sonnet-4-20250514", result.Provider.ModelId);
        Assert.Equal("claude-sonnet-4-20250514", result.Provider.WireModel);
        Assert.Equal("sk-ant-secret", result.Provider.ApiKey);
        Assert.Null(result.Provider.BearerToken);
        Assert.NotNull(result.Provider.Headers);
        Assert.Equal("2023-06-01", result.Provider.Headers!["anthropic-version"]);
    }

    [Fact]
    public async Task ResolveAsync_MapsVersionedDeploymentUrlToStableSdkAzureRoute()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Code:Copilot:Providers:Gateway:type"] = "openai",
            ["Code:Copilot:Providers:Gateway:url"] = "https://gateway.example/providers/openai/deployments/wire-model",
            ["Code:Copilot:Providers:Gateway:model"] = "known-model",
            ["Code:Copilot:Providers:Gateway:apiVersion"] = "2025-01-01",
            ["Code:Copilot:Providers:Gateway:authType"] = "bearer",
            ["Code:Copilot:Providers:Gateway:bearerToken"] = "gateway-token"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("Gateway", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("azure", result.Provider.Type);
        Assert.Equal("completions", result.Provider.WireApi);
        Assert.Equal("https://gateway.example/providers/openai/", result.Provider.BaseUrl);
        Assert.Equal("known-model", result.Provider.ModelId);
        Assert.Equal("wire-model", result.Provider.WireModel);
        Assert.Equal("2025-01-01", result.Provider.Azure?.ApiVersion);
        Assert.Equal("gateway-token", result.Provider.BearerToken);
        Assert.Null(result.Provider.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_AcceptsClaudeAliasForAnthropicProvider()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Code:Copilot:Providers:anthropic:provider"] = "anthropic",
            ["Code:Copilot:Providers:anthropic:type"] = "anthropic",
            ["Code:Copilot:Providers:anthropic:url"] = "https://api.anthropic.com/v1",
            ["Code:Copilot:Providers:anthropic:model"] = "claude-sonnet-4-20250514",
            ["Code:Copilot:Providers:anthropic:authType"] = "api_key",
            ["Code:Copilot:Providers:anthropic:apiKey"] = "sk-ant-secret"
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("claude", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("claude", result.ProviderName);
        Assert.Equal("anthropic", result.Provider.Type);
        Assert.Equal("messages", result.Provider.WireApi);
        Assert.Equal("sk-ant-secret", result.Provider.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_LoadsRawJsonProviderSectionValue()
    {
        using var services = CreateServices(new Dictionary<string, string?>
        {
            ["Code:Copilot:Providers:RawJsonProvider"] = """
            {
              "url": "https://api.openai.com/v1",
              "type": "openai",
              "model": "gpt-4.1-mini",
              "authType": "api_key",
              "apiKey": "sk-json"
            }
            """
        });
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("RawJsonProvider", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("gpt-4.1-mini", result.Model);
        Assert.Equal("sk-json", result.Provider.ApiKey);
    }

    [Fact]
    public async Task ResolveAsync_ThrowsMcpExceptionWhenProviderDoesNotExist()
    {
        using var services = CreateServices();
        var resolver = CreateResolver(services);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            resolver.ResolveAsync("MissingProvider", "fallback-model", null, CancellationToken.None));

        Assert.Contains("MissingProvider", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("was not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider CreateServices(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();
        var typedSettings = new CodeServerSettings();
        new CodeServerSettingsOptionsConfigurator(configuration).Configure(typedSettings);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IOptions<CodeServerSettings>>(Options.Create(typedSettings));
        services.AddHttpClient(nameof(ConfigurationCopilotProviderConfigResolver));
        return services.BuildServiceProvider();
    }

    private static ConfigurationCopilotProviderConfigResolver CreateResolver(ServiceProvider services)
        => new(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IOptions<CodeServerSettings>>(),
            services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ConfigurationCopilotProviderConfigResolver>.Instance);
}
