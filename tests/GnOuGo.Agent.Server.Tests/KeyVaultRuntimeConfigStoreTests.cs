using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class KeyVaultRuntimeConfigStoreTests
{
    [Fact]
    public async Task BuildEffectiveOptionsAsync_LoadsTrustedSecretsAndKeepsKeyVaultMcpAvailable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tests-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<KeyVaultService>();
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();

        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
                await db.Database.EnsureCreatedAsync();

                var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
                await keyVault.EnsureDefaultKeyPairAsync();
                await keyVault.SetSecretAsync(
                    "LLM--Models--openai",
                    "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"gpt-4.1\",\"authType\":\"api_key\",\"apiKey\":\"top-secret\"}",
                    null,
                    "test",
                    CancellationToken.None);
                await keyVault.SetSecretAsync(
                    "LLM--McpServers--Github",
                    "{\"name\":\"Github\",\"transport\":\"http\",\"description\":\"GitHub automation\",\"discoveryTimeoutSeconds\":120,\"callTimeoutSeconds\":1200,\"url\":\"https://api.githubcopilot.com/mcp/\",\"authType\":\"api_key\",\"apiKey\":\"gh-secret\"}",
                    null,
                    "test",
                    CancellationToken.None);
                await keyVault.SetSecretAsync(
                    "LLM--McpServers--GnOuGo.KeyVault.Mcp",
                    "{\"name\":\"GnOuGo.KeyVault.Mcp\",\"transport\":\"http\",\"description\":\"secret manager\",\"url\":\"http://127.0.0.1:0/mcp/keyvault\"}",
                    null,
                    "test",
                    CancellationToken.None);
            }

            var store = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
            var baseOptions = new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase),
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GnOuGo.KeyVault.Mcp"] = new()
                    {
                        Type = "http",
                        Url = "http://127.0.0.1:0/mcp/keyvault",
                        DiscoveryTimeoutSeconds = 90,
                        CallTimeoutSeconds = 600
                    }
                }
            };

            var effective = await store.BuildEffectiveOptionsAsync(baseOptions, CancellationToken.None);

            Assert.True(effective.Models.TryGetValue("openai", out var providerConfig));
            Assert.NotNull(providerConfig);
            Assert.Equal("https://api.openai.com/v1", providerConfig.Url);
            Assert.Equal("top-secret", providerConfig.ApiKey);
            Assert.Equal("gpt-4.1", effective.DefaultModel);

            Assert.True(effective.McpServers.TryGetValue("Github", out var github));
            Assert.NotNull(github);
            Assert.Equal("http", github.Type);
            Assert.Equal("https://api.githubcopilot.com/mcp/", github.Url);
            Assert.Equal("gh-secret", github.ApiKey);
            Assert.Equal(120, github.DiscoveryTimeoutSeconds);
            Assert.Equal(1200, github.CallTimeoutSeconds);
            Assert.True(effective.McpServers.TryGetValue("GnOuGo.KeyVault.Mcp", out var keyVaultServer));
            Assert.NotNull(keyVaultServer);
            Assert.Equal("http", keyVaultServer.Type);
            Assert.Equal("http://127.0.0.1:0/mcp/keyvault", keyVaultServer.Url);
            Assert.Equal(90, keyVaultServer.DiscoveryTimeoutSeconds);
            Assert.Equal(600, keyVaultServer.CallTimeoutSeconds);
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a temporary SQLite file.
            }
        }
    }

    [Fact]
    public async Task BuildEffectiveOptionsAsync_LoadsLegacySecretNamesAndLegacyJsonFields()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tests-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<KeyVaultService>();
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();

        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
                await db.Database.EnsureCreatedAsync();

                var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
                await keyVault.EnsureDefaultKeyPairAsync();
                await keyVault.SetSecretAsync(
                    "gnougo_llm_openai",
                    "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"gpt-4.1\",\"auth_type\":\"api_key\",\"api_key\":\"legacy-secret\"}",
                    null,
                    "test",
                    CancellationToken.None);
            }

            var store = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
            var effective = await store.BuildEffectiveOptionsAsync(new LLMOptions(), CancellationToken.None);

            Assert.True(effective.Models.TryGetValue("openai", out var providerConfig));
            Assert.NotNull(providerConfig);
            Assert.Equal("legacy-secret", providerConfig.ApiKey);
            Assert.Equal("gpt-4.1", effective.DefaultModel);
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a temporary SQLite file.
            }
        }
    }
}



