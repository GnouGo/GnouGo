using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class KeyVaultCopilotProviderConfigResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-copilot-provider-tests-" + Guid.NewGuid().ToString("N"));

    public KeyVaultCopilotProviderConfigResolverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenProviderIsOmitted()
    {
        using var services = CreateServices();
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync(null, "fallback-model", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_LoadsAgentServerProviderSecretFromKeyVault()
    {
        using var services = CreateServices();
        await SaveSecretAsync(services, "LLM--Models--CustomCopilot", """
        {
          "provider": "CustomCopilot",
          "url": "https://models.github.ai/inference",
          "type": "copilot",
          "model": "gpt-5.4-mini",
          "authType": "api_key",
          "apiKey": "ghp_secret"
        }
        """);
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("CustomCopilot", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("CustomCopilot", result.ProviderName);
        Assert.Equal("gpt-5.4-mini", result.Model);
        Assert.Equal("openai", result.Provider.Type);
        Assert.Equal("chat-completions", result.Provider.WireApi);
        Assert.Equal("https://models.github.ai/inference", result.Provider.BaseUrl);
        Assert.Equal("gpt-5.4-mini", result.Provider.ModelId);
        Assert.Equal("gpt-5.4-mini", result.Provider.WireModel);
        Assert.Null(result.Provider.ApiKey);
        Assert.Equal("ghp_secret", result.Provider.BearerToken);
    }

    [Fact]
    public async Task ResolveAsync_UsesLegacyProviderSecretName()
    {
        using var services = CreateServices();
        await SaveSecretAsync(services, "gnougo_llm_LegacyProvider", """
        {
          "url": "https://api.openai.com/v1",
          "type": "openai",
          "model": "gpt-4.1",
          "authType": "api_key",
          "apiKey": "sk-secret"
        }
        """);
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("LegacyProvider", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("gpt-4.1", result.Model);
        Assert.Equal("sk-secret", result.Provider.ApiKey);
        Assert.Null(result.Provider.BearerToken);
    }

    [Fact]
    public async Task ResolveAsync_MapsClaudeProviderToAnthropicMessagesWireApi()
    {
        using var services = CreateServices();
        await SaveSecretAsync(services, "LLM--Models--claude", """
        {
          "provider": "claude",
          "type": "claude",
          "url": "https://api.anthropic.com/v1",
          "model": "claude-sonnet-4-20250514",
          "authType": "api_key",
          "apiKey": "sk-ant-secret"
        }
        """);
        var resolver = CreateResolver(services);

        var result = await resolver.ResolveAsync("claude", "fallback-model", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("claude", result.ProviderName);
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
    public async Task ResolveAsync_ThrowsMcpExceptionWhenProviderDoesNotExist()
    {
        using var services = CreateServices();
        var resolver = CreateResolver(services);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            resolver.ResolveAsync("MissingProvider", "fallback-model", null, CancellationToken.None));

        Assert.Contains("MissingProvider", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("was not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var dbPath = Path.Combine(_root, "keyvault.db");
        services.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<KeyVaultService>();
        services.AddHttpClient(nameof(KeyVaultCopilotProviderConfigResolver));
        return services.BuildServiceProvider();
    }

    private static KeyVaultCopilotProviderConfigResolver CreateResolver(ServiceProvider services)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<KeyVaultCopilotProviderConfigResolver>.Instance);

    private static async Task SaveSecretAsync(ServiceProvider services, string key, string value)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
        await KeyVaultDatabaseBootstrap.EnsureCreatedAsync(db);
        var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        await keyVault.EnsureDefaultKeyPairAsync();
        await keyVault.SetSecretAsync(key, value, null, "test");
    }
}

