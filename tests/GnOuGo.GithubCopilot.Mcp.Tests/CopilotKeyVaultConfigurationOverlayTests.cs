using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CopilotKeyVaultConfigurationOverlayTests
{
    [Fact]
    public async Task LoadAsync_ProducesHighestPriorityTypedSettingsOverlay()
    {
        var reader = new FakeCatalogReader()
            .Add(
                "gnougo_llm_OpenAi",
                """{"url":"https://legacy.example/v1","wireApi":"legacy"}""")
            .Add(
                "LLM--Models--OpenAi",
                """
                {
                  "provider": "OpenAi",
                  "url": "https://keyvault.example/v1",
                  "type": "openai",
                  "model": "provider-model",
                  "authType": "api_key",
                  "apiKey": "keyvault-api-key"
                }
                """)
            .Add(
                CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "Model",
                "mcp-model")
            .Add(
                CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "RequestTimeoutSeconds",
                "600")
            .Add(
                CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "EnableSandboxBypassGrants",
                "true")
            .Add(
                CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "Telemetry--Enabled",
                "false")
            .Add(
                CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "TokenEnvironmentVariables--0",
                "KEYVAULT_TOKEN")
            .Add(
                CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "Providers--OpenAi--model",
                "mcp-provider-model");

        var overlay = await CopilotKeyVaultConfigurationOverlay.LoadAsync(
            reader,
            TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Code:Copilot:Model"] = "environment-model",
                ["Code:Copilot:RequestTimeoutSeconds"] = "42",
                ["Code:Copilot:Telemetry:Enabled"] = "true",
                ["Code:Copilot:TokenEnvironmentVariables:0"] = "ENV_TOKEN",
                ["Code:Copilot:Providers:OpenAi:url"] = "https://environment.example/v1",
                ["Code:Copilot:Providers:OpenAi:model"] = "environment-provider-model"
            })
            .AddInMemoryCollection(overlay.Values)
            .Build();
        var settings = new CodeServerSettings();

        new CodeServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Null(overlay.Warning);
        Assert.Equal("mcp-model", settings.Copilot.Model);
        Assert.Equal(600, settings.Copilot.RequestTimeoutSeconds);
        Assert.True(settings.Copilot.EnableSandboxBypassGrants);
        Assert.False(settings.Copilot.Telemetry.Enabled);
        Assert.Equal(["KEYVAULT_TOKEN"], settings.Copilot.TokenEnvironmentVariables);
        var provider = Assert.Contains("OpenAi", settings.Copilot.Providers);
        Assert.Equal("https://keyvault.example/v1", provider.Url);
        Assert.Equal("mcp-provider-model", provider.Model);
        Assert.Equal("keyvault-api-key", provider.ApiKey);
        Assert.Null(provider.WireApi);
    }

    [Fact]
    public async Task LoadAsync_PreservesAgentDefaultProviderSelectionThroughTypedSettings()
    {
        var reader = new FakeCatalogReader().Add(
            "LLM--Models--OpenAi",
            """
            {
              "url": "https://api.openai.com/v1",
              "type": "openai",
              "model": "agent-provider-model",
              "authType": "api_key",
              "apiKey": "provider-secret"
            }
            """);
        var overlay = await CopilotKeyVaultConfigurationOverlay.LoadAsync(
            reader,
            TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GNouGo:DefaultLlmProvider"] = "OpenAi"
            })
            .AddInMemoryCollection(overlay.Values)
            .Build();
        var settings = new CodeServerSettings();
        new CodeServerSettingsOptionsConfigurator(configuration).Configure(settings);
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(ConfigurationCopilotProviderConfigResolver));
        await using var provider = services.BuildServiceProvider();
        var resolver = new ConfigurationCopilotProviderConfigResolver(
            configuration,
            Options.Create(settings),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ConfigurationCopilotProviderConfigResolver>.Instance);

        var resolved = await resolver.ResolveAsync(
            "Copilot",
            "fallback-model",
            fallbackBearerToken: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal("OpenAi", resolved.ProviderName);
        Assert.Equal("agent-provider-model", resolved.Model);
        Assert.Equal("provider-secret", resolved.Provider.ApiKey);
    }

    [Fact]
    public async Task LoadAsync_ReturnsRedactedFallbackWhenKeyVaultIsUnavailable()
    {
        var reader = new FakeCatalogReader
        {
            Failure = new KeyVaultAccessException(
                "contains-sensitive-storage-details",
                new IOException("contains-sensitive-path"))
        };

        var overlay = await CopilotKeyVaultConfigurationOverlay.LoadAsync(
            reader,
            TestContext.Current.CancellationToken);

        Assert.Empty(overlay.Values);
        Assert.NotNull(overlay.Warning);
        Assert.DoesNotContain("sensitive", overlay.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_FailsForMalformedPresentProviderJson()
    {
        var reader = new FakeCatalogReader().Add("LLM--Models--OpenAi", "{");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CopilotKeyVaultConfigurationOverlay.LoadAsync(
                reader,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_FailsForAmbiguousOrInvalidTypedOverrides()
    {
        var ambiguous = new FakeCatalogReader()
            .Add(CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "Model", "first")
            .Add(CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "model", "second");
        var invalid = new FakeCatalogReader()
            .Add(CopilotKeyVaultConfigurationOverlay.McpOverridePrefix + "EnableSandboxBypassGrants", "not-boolean");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CopilotKeyVaultConfigurationOverlay.LoadAsync(
                ambiguous,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CopilotKeyVaultConfigurationOverlay.LoadAsync(
                invalid,
                TestContext.Current.CancellationToken));
    }

    private sealed class FakeCatalogReader : IKeyVaultSecretCatalogReader
    {
        private readonly List<KeyVaultSecretLookupResult> _values = [];

        public Exception? Failure { get; init; }

        public FakeCatalogReader Add(string key, string value)
        {
            _values.Add(new KeyVaultSecretLookupResult(key, value));
            return this;
        }

        public Task<string?> GetDefaultTenantSecretValueAsync(
            string key,
            string? author = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(_values.FirstOrDefault(value =>
                string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase))?.Value);
        }

        public async Task<KeyVaultSecretLookupResult?> GetFirstDefaultTenantSecretValueAsync(
            IEnumerable<string> candidateKeys,
            string? author = null,
            CancellationToken ct = default)
        {
            foreach (var key in candidateKeys)
            {
                var value = await GetDefaultTenantSecretValueAsync(key, author, ct);
                if (value is not null)
                    return new KeyVaultSecretLookupResult(key, value);
            }

            return null;
        }

        public Task<IReadOnlyList<KeyVaultSecretLookupResult>> GetDefaultTenantSecretValuesByPrefixAsync(
            string keyPrefix,
            string? author = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            IReadOnlyList<KeyVaultSecretLookupResult> results = _values
                .Where(value => value.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Task.FromResult(results);
        }
    }
}
