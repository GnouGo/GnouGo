using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.KeyVault.Mcp;

namespace GnOuGo.Agent.Server.Tests;

public sealed class KeyVaultRuntimeConfigStoreTests
{
    [Fact]
    public async Task BuildEffectiveOptionsAsync_LoadsTrustedSecretsAndKeepsKeyVaultMcpAvailable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tests-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyVaultMcpPersistence(dbPath);
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();

        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                await provider.InitializeKeyVaultMcpAsync(ct: TestContext.Current.CancellationToken);
                var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
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
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new()
                    {
                        Url = "https://placeholder.invalid",
                        Type = "openai",
                        RequestPolicy = new LLMProviderRequestPolicyOptions
                        {
                            BackgroundProtocol = LLMBackgroundProtocolMode.ChatCompletions,
                            UnspecifiedOutputTokens = LLMUnspecifiedOutputTokensMode.Configured,
                            DefaultMaxOutputTokens = 4_096,
                            MaxOutputTokensCap = 8_192
                        },
                        RetryPolicy = new LLMProviderRetryPolicyOptions
                        {
                            MaxAttempts = 2,
                            BaseDelayMilliseconds = 250,
                            MaxDelayMilliseconds = 2_000,
                            MaxTotalDelayMilliseconds = 5_000
                        }
                    }
                },
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
            Assert.Equal(LLMBackgroundProtocolMode.ChatCompletions, providerConfig.RequestPolicy.BackgroundProtocol);
            Assert.Equal(4_096, providerConfig.RequestPolicy.DefaultMaxOutputTokens);
            Assert.Equal(2, providerConfig.RetryPolicy.MaxAttempts);
            Assert.Equal(5_000, providerConfig.RetryPolicy.MaxTotalDelayMilliseconds);

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
        services.AddKeyVaultMcpPersistence(dbPath);
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();

        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                await provider.InitializeKeyVaultMcpAsync(ct: TestContext.Current.CancellationToken);
                var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
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

    [Fact]
    public async Task BuildEffectiveOptionsAsync_AppliesBundledMcpFieldOverrideToEnvironment()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tests-{Guid.NewGuid():N}.db");
        var settings = new BundledMcpSettings
        {
            Servers = new Dictionary<string, BundledMcpServerSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["GnOuGo.Git.Mcp"] = new()
                {
                    Listable = true,
                    EditableFields = new Dictionary<string, BundledMcpEditableFieldSettings>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["git_token"] = new()
                        {
                            SecretKey = "LLM--McpServerOverrides--GnOuGo.Git.Mcp--Git--Token",
                            Target = "env:Git__Token"
                        }
                    }
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyVaultMcpPersistence(dbPath);
        services.AddSingleton<IOptions<BundledMcpSettings>>(Options.Create(settings));
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();

        await using var provider = services.BuildServiceProvider();

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                await provider.InitializeKeyVaultMcpAsync(ct: TestContext.Current.CancellationToken);
                var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
                await keyVault.SetSecretAsync(
                    "LLM--McpServerOverrides--GnOuGo.Git.Mcp--Git--Token",
                    "ghp-runtime-token",
                    null,
                    "test",
                    CancellationToken.None);
            }

            var store = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
            var baseOptions = new LLMOptions
            {
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GnOuGo.Git.Mcp"] = new()
                    {
                        Type = "stdio",
                        Command = "tools/GnOuGo.Git.Mcp/GnOuGo.Git.Mcp",
                        Args = []
                    }
                }
            };

            var effective = await store.BuildEffectiveOptionsAsync(baseOptions, CancellationToken.None);

            var git = Assert.Contains("GnOuGo.Git.Mcp", effective.McpServers);
            Assert.Equal("tools/GnOuGo.Git.Mcp/GnOuGo.Git.Mcp", git.Command);
            Assert.Equal("ghp-runtime-token", git.EnvironmentVariables?["Git__Token"]);
            Assert.Null(git.ApiKey);
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

    [Theory]
    [InlineData("custom/keyvault.db", "custom/keyvault.db")]
    [InlineData(null, ".GnOuGo/data/gnougo-keyvault.db")]
    public async Task BuildEffectiveOptionsAsync_PropagatesOnlyKeyVaultLocationToDirectReaderMcp(
        string? configuredDatabasePath,
        string expectedDatabasePath)
    {
        const string serverName = "GnOuGo.GithubCopilot.Mcp";
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-keyvault-tests-{Guid.NewGuid():N}.db");
        var targetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["provider"] = "Provider",
            ["model"] = "Model",
            ["reasoning_effort"] = "ReasoningEffort",
            ["use_logged_in_user"] = "UseLoggedInUser",
            ["request_timeout_seconds"] = "RequestTimeoutSeconds",
            ["managed_session_ttl_seconds"] = "ManagedSessionTtlSeconds"
        };
        var settings = new BundledMcpSettings
        {
            Servers = new Dictionary<string, BundledMcpServerSettings>(StringComparer.OrdinalIgnoreCase)
            {
                [serverName] = new()
                {
                    Listable = true,
                    ReadsKeyVaultDirectly = true,
                    EditableFields = targetNames.ToDictionary(
                        entry => entry.Key,
                        entry => new BundledMcpEditableFieldSettings
                        {
                            SecretKey = $"LLM--McpServerOverrides--{serverName}--Code--Copilot--{entry.Value}"
                        },
                        StringComparer.OrdinalIgnoreCase)
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyVaultMcpPersistence(dbPath);
        services.AddSingleton<IOptions<BundledMcpSettings>>(Options.Create(settings));
        services.AddSingleton<IOptions<KeyVaultSettings>>(Options.Create(new KeyVaultSettings
        {
            DatabasePath = configuredDatabasePath
        }));
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();

        await using var provider = services.BuildServiceProvider();
        try
        {
            await provider.InitializeKeyVaultMcpAsync(ct: TestContext.Current.CancellationToken);
            await using (var scope = provider.CreateAsyncScope())
            {
                var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
                await keyVault.SetSecretAsync(
                    "LLM--Models--openai",
                    "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"provider-model\",\"authType\":\"api_key\",\"apiKey\":\"provider-secret\"}",
                    null,
                    "test",
                    CancellationToken.None);
                var values = new Dictionary<string, string>
                {
                    ["Provider"] = "openai",
                    ["Model"] = "fallback-model",
                    ["ReasoningEffort"] = "medium",
                    ["UseLoggedInUser"] = "false",
                    ["RequestTimeoutSeconds"] = "600",
                    ["ManagedSessionTtlSeconds"] = "300"
                };
                foreach (var entry in values)
                {
                    await keyVault.SetSecretAsync(
                        $"LLM--McpServerOverrides--{serverName}--Code--Copilot--{entry.Key}",
                        entry.Value,
                        null,
                        "test",
                        CancellationToken.None);
                }
            }

            var baseOptions = new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "provider-model",
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [serverName] = new()
                    {
                        Type = "stdio",
                        Command = "tools/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp",
                        Args = ["--stdio"]
                    }
                }
            };

            var store = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
            var effective = await store.BuildEffectiveOptionsAsync(baseOptions, CancellationToken.None);

            var copilot = Assert.Contains(serverName, effective.McpServers);
            Assert.Equal("tools/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp", copilot.Command);
            Assert.Equal(["--stdio"], copilot.Args);
            var environment = Assert.IsType<Dictionary<string, string?>>(copilot.EnvironmentVariables);
            Assert.Single(environment);
            Assert.Equal(expectedDatabasePath, environment["KeyVault__DatabasePath"]);

            var configuredProvider = Assert.Contains("openai", effective.Models);
            Assert.Equal("provider-secret", configuredProvider.ApiKey);
            Assert.Equal("https://api.openai.com/v1", configuredProvider.Url);
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
