using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmRuntimeOptionsStoreTests
{
    [Fact]
    public void Constructor_PreservesConfiguredMcpTimeouts()
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai" }
                },
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GnOuGo.Git.Mcp"] = new()
                    {
                        Type = "stdio",
                        Description = "Git repository workflows via stdio MCP server using LibGit2Sharp",
                        DiscoveryTimeoutSeconds = 120,
                        CallTimeoutSeconds = 1200,
                        Command = "dotnet",
                        Args = ["run", "--project", "src/GnOuGo.Git.Mcp/GnOuGo.Git.Mcp.csproj"]
                    }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        var gitMcp = store.Current.McpServers["GnOuGo.Git.Mcp"];
        Assert.Equal(120, gitMcp.DiscoveryTimeoutSeconds);
        Assert.Equal(1200, gitMcp.CallTimeoutSeconds);
    }

    [Fact]
    public void UpdateProvider_WhenTransientMountedMcpServersExist_KeepsEverythingInMemoryOnly()
    {
        var userSettingsPath = Path.Combine(Path.GetTempPath(), "gnougo-agent-server-tests", "runtime-settings-regression", $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userSettingsPath)!);

        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                },
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["github"] = new() { Type = "http", Url = "https://api.githubcopilot.com/mcp/", Description = "GitHub" }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.UpsertTransientMcpServer("GnOuGo.Agent.Mcp", new McpServerOptions
        {
            Type = "http",
            Url = "http://127.0.0.1:64000/mcp/agent",
            Description = "Mounted Agent MCP"
        });

        var updated = store.SetDefaultProvider("openai", "gpt-4o-mini");
        Assert.True(updated);
        Assert.False(File.Exists(userSettingsPath));

        Assert.True(store.Current.McpServers.ContainsKey("github"));
        Assert.True(store.Current.McpServers.ContainsKey("GnOuGo.Agent.Mcp"));
    }

    [Fact]
    public void ReplaceRuntimeOptions_OverwritesTheLiveSnapshot()
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai" }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.ReplaceRuntimeOptions(new LLMOptions
        {
            DefaultProvider = "ollama",
            DefaultModel = "llama3.2",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
            },
            McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Github"] = new() { Type = "http", Url = "https://api.githubcopilot.com/mcp/" }
            }
        });

        Assert.Equal("ollama", store.Current.DefaultProvider, ignoreCase: true);
        Assert.Equal("llama3.2", store.Current.DefaultModel);
        Assert.True(store.Current.Models.ContainsKey("ollama"));
        Assert.True(store.Current.McpServers.ContainsKey("Github"));
    }

    [Fact]
    public void UpdateProvider_InitializesProviderTypeFromKeyForApiKeyAuth()
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions()),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.UpdateProvider(
            providerKey: "anthropic",
            url: "https://api.anthropic.com/v1",
            model: "claude-sonnet-4-20250514",
            apiKey: "sk-ant-secret",
            authType: "api_key");

        Assert.True(store.Current.Models.TryGetValue("anthropic", out var provider));
        Assert.NotNull(provider);
        Assert.Equal("anthropic", provider.Type);
        Assert.Equal("anthropic", provider.ResolvedType);
    }

    [Fact]
    public void UpdateProvider_SwitchingOpenAiFromApiKeyToOidcClearsApiKey()
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "old-secret" }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.UpdateProvider(
            providerKey: "openai",
            url: "https://api.openai.com/v1",
            model: "gpt-4o",
            apiKey: "",
            authType: "oidc",
            oidcIssuer: "https://issuer.example.com",
            oidcClientId: "openai-client",
            oidcScopes: "api://openai/.default",
            oidcClientSecret: "oidc-secret",
            apiVersion: "2025-01-01-preview");

        var provider = store.Current.ResolveProvider("openai");
        Assert.NotNull(provider);
        Assert.Equal("openai", provider.Type);
        Assert.Null(provider.ApiKey);
        Assert.Equal("https://issuer.example.com", provider.Issuer);
        Assert.Equal("openai-client", provider.ClientId);
        Assert.Equal("api://openai/.default", provider.Scopes);
        Assert.Equal("oidc-secret", provider.ClientSecret);
        Assert.Equal("2025-01-01-preview", provider.ApiVersion);
        Assert.Equal("gpt-4o", store.Current.DefaultModel);
    }

    [Fact]
    public void UpdateProvider_SwitchingOpenAiFromOidcToApiKeyClearsOidcFields()
    {
        var store = new LLMRuntimeOptionsStore(
            Options.Create(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new()
                    {
                        Url = "https://api.openai.com/v1",
                        Type = "openai",
                        Issuer = "https://issuer.example.com",
                        ClientId = "openai-client",
                        Scopes = "api://openai/.default",
                        ClientSecret = "oidc-secret",
                        ApiVersion = "2025-01-01-preview"
                    }
                }
            }),
            NullLogger<LLMRuntimeOptionsStore>.Instance);

        store.UpdateProvider(
            providerKey: "openai",
            url: "https://api.openai.com/v1",
            model: "gpt-4o-mini",
            apiKey: "new-secret",
            authType: "api_key");

        var provider = store.Current.ResolveProvider("openai");
        Assert.NotNull(provider);
        Assert.Equal("openai", provider.Type);
        Assert.Equal("new-secret", provider.ApiKey);
        Assert.Null(provider.Issuer);
        Assert.Null(provider.ClientId);
        Assert.Null(provider.Scopes);
        Assert.Null(provider.ClientSecret);
        Assert.Null(provider.ApiVersion);
        Assert.Equal("gpt-4o-mini", store.Current.DefaultModel);
    }
}


