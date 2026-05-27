using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Endpoints;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Shared;
using GnOuGo.Agent.Mcp;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using OtlpTenantCollector.Models;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ConfigureProvidersServiceTests
{
    [Fact]
    public void ShouldUseInjectedModelCatalog_ReturnsFalse_WhenMatchingRuntimeOpenAiHasNoAuthButWizardDoes()
    {
        var llm = new RecordingLlmClient();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai" }
            }
        });

        var service = new ConfigureProvidersService(
            llm,
            new AgentHumanInputProvider(),
            new FakeModelCatalog(),
            new FakeKeyVaultRuntimeConfigStore(),
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var useInjected = ConfigureProvidersServiceTestHelpers.InvokeShouldUseInjectedModelCatalog(
            service,
            "openai",
            new ModelProviderOptions
            {
                Url = "https://api.openai.com/v1",
                Type = "openai",
                ApiKey = "wizard-secret"
            });

        Assert.False(useInjected);
    }

    [Fact]
    public void ShouldUseInjectedModelCatalog_ReturnsTrue_WhenRuntimeProviderDoesNotExist()
    {
        var llm = new RecordingLlmClient();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = string.Empty,
            DefaultModel = string.Empty,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        var service = new ConfigureProvidersService(
            llm,
            new AgentHumanInputProvider(),
            new FakeModelCatalog(),
            new FakeKeyVaultRuntimeConfigStore(),
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var useInjected = ConfigureProvidersServiceTestHelpers.InvokeShouldUseInjectedModelCatalog(
            service,
            "openai",
            new ModelProviderOptions
            {
                Url = "https://api.openai.com/v1",
                Type = "openai",
                ApiKey = "wizard-secret"
            });

        Assert.True(useInjected);
    }

    [Fact]
    public void ShouldUseInjectedModelCatalog_ReturnsTrue_WhenRuntimeProviderHasMatchingAuth()
    {
        var llm = new RecordingLlmClient();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "runtime-secret" }
            }
        });

        var service = new ConfigureProvidersService(
            llm,
            new AgentHumanInputProvider(),
            new FakeModelCatalog(),
            new FakeKeyVaultRuntimeConfigStore(),
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var useInjected = ConfigureProvidersServiceTestHelpers.InvokeShouldUseInjectedModelCatalog(
            service,
            "openai",
            new ModelProviderOptions
            {
                Url = "https://api.openai.com/v1",
                Type = "openai",
                ApiKey = "runtime-secret"
            });

        Assert.True(useInjected);
    }

    [Fact]
    public void ShouldUseInjectedModelCatalog_ReturnsFalse_WhenRuntimeUsesApiKeyButWizardUsesOidc()
    {
        var llm = new RecordingLlmClient();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "runtime-secret" }
            }
        });

        var service = new ConfigureProvidersService(
            llm,
            new AgentHumanInputProvider(),
            new FakeModelCatalog(),
            new FakeKeyVaultRuntimeConfigStore(),
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var useInjected = ConfigureProvidersServiceTestHelpers.InvokeShouldUseInjectedModelCatalog(
            service,
            "openai",
            new ModelProviderOptions
            {
                Url = "https://api.openai.com/v1",
                Type = "openai",
                Issuer = "https://issuer.example.com",
                ClientId = "client-id",
                Scopes = "scope.default"
            });

        Assert.False(useInjected);
    }

    [Fact]
    public async Task ExecuteAsync_LlmHelp_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var service = SmartFlowTestFactory.CreateProvidersService(llm);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Contains("# 🤖 LLM Provider Commands", answer.Text);
        Assert.Contains("`/llm add`", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmbeddingHelp_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var service = SmartFlowTestFactory.CreateProvidersService(llm);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/embedding", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Contains("# 🧬 Embedding Model Commands", answer.Text);
        Assert.Contains("`/embedding add`", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_McpUnknownCommand_ReturnsHelpWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var service = SmartFlowTestFactory.CreateProvidersService(llm);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp nope", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Contains("Unknown `/mcp` command", answer.Text);
        Assert.Contains("# 🔌 MCP Server Commands", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--copilot", "{}", 3, "2026-04-01T12:13:24.4824726+00:00")
            .AddSecret("LLM--Models--openai", "{}", 1, "2026-03-26T17:16:58.7030088+00:00")
            .AddSecret("LLM--McpServers--github", "{}", 2, "2026-04-01T12:15:00+00:00");

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🤖 Configured LLM Providers", answer.Text);
        Assert.Contains("| Provider | Default | Model | Key | Version | Stored |", answer.Text);
        Assert.Contains("| openai | ✅ yes | gpt-4o-mini | `LLM--Models--openai` | 1 |", answer.Text);
        Assert.Contains("| copilot |", answer.Text);
        Assert.Contains("`LLM--Models--copilot`", answer.Text);
        Assert.DoesNotContain("LLM--McpServers--github", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmDefault_UpdatesRuntimeDefaultAndPersistsSelectedModel()
    {
        var llm = new RecordingLlmClient();
        var modelCatalog = new FakeModelCatalog()
            .Add("ollama",
                new LLMModelDescriptor("llama3", "llama3", "ollama", "ollama"),
                new LLMModelDescriptor("llama3:8b", "llama3:8b", "ollama", "ollama"));
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--ollama", "{\"provider\":\"ollama\",\"url\":\"http://localhost:11434\",\"model\":\"llama3\",\"authType\":\"none\"}");
        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
            },
            ModelOverrides = TestModelOverrides("llama3:8b")
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_default.provider" => new JsonObject { ["provider"] = "ollama" },
                    "llm_model.select.ollama" => new JsonObject { ["model"] = "llama3:8b" },
                    "llm_default.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_default.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            modelCatalog,
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm default", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ Default LLM provider set to 'ollama' with model 'llama3:8b'.");
        Assert.Equal("ollama", runtimeStore.Current.DefaultProvider);
        Assert.Equal("llama3:8b", runtimeStore.Current.DefaultModel);

        var configJson = await keyVaultStore.GetSecretValueAsync("LLM--Models--ollama", CancellationToken.None);
        Assert.NotNull(configJson);
        var config = JsonNode.Parse(configJson)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("llama3:8b", config["model"]?.GetValue<string>());
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_PromptsForMetadata_WhenSelectedModelIsUnknown()
    {
        var llm = new RecordingLlmClient();
        var modelCatalog = new FakeModelCatalog()
            .Add("ollama", new LLMModelDescriptor("local/custom", "local/custom", "local", "ollama"));
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = string.Empty,
            DefaultModel = string.Empty,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "ollama" },
                    "llm_add.connection" => new JsonObject { ["url"] = "http://localhost:11434" },
                    "llm_model.select.ollama" => new JsonObject { ["model"] = "local/custom" },
                    "llm_model.metadata.ollama" => new JsonObject
                    {
                        ["display_name"] = "Local Custom",
                        ["owned_by"] = "local",
                        ["context_window_tokens"] = 32768,
                        ["max_input_tokens"] = 32768,
                        ["max_output_tokens"] = 4096,
                        ["input_price_per_1m_tokens"] = 0,
                        ["output_price_per_1m_tokens"] = 0,
                        ["supports_temperature"] = true,
                        ["supports_reasoning_effort"] = false,
                        ["supports_structured_output"] = true,
                        ["supports_tools"] = true,
                        ["supports_json_mode"] = true,
                        ["supports_vision"] = "unknown",
                        ["supports_audio"] = "unknown"
                    },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);
                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            modelCatalog,
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "thinking:info" && evt.Text?.Contains("Metadata for model 'local/custom'", StringComparison.Ordinal) == true);
        Assert.True(runtimeStore.Current.ModelOverrides.TryGetValue("local/custom", out var metadata));
        Assert.Equal(32768, metadata.ContextWindowTokens);
        Assert.Equal(0m, metadata.Pricing!.InputPer1MTokens);
        Assert.Null(metadata.Pricing.CachedInputPer1MTokens);
        Assert.True(metadata.Capabilities.SupportsTools);
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_OverwritingDefaultOpenAiApiKeyWithOidcUpdatesRuntimeAndSecret()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--openai", "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"gpt-4o-mini\",\"authType\":\"api_key\",\"apiKey\":\"old-secret\"}");
        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "old-secret" }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "openai" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "oidc" },
                    "llm.auth.oidc" => new JsonObject
                    {
                        ["issuer"] = "https://issuer.example.com",
                        ["client_id"] = "openai-client",
                        ["scopes"] = "api://openai/.default",
                        ["client_secret"] = "oidc-secret",
                        ["api_version"] = "2025-01-01-preview"
                    },
                    "llm_model.manual.openai" => new JsonObject { ["model"] = "gpt-4o" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);
                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            new FakeModelCatalog(),
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text?.Contains("LLM provider 'openai' saved", StringComparison.Ordinal) == true);
        var runtimeOpenAi = runtimeStore.Current.ResolveProvider("openai");
        Assert.NotNull(runtimeOpenAi);
        Assert.Null(runtimeOpenAi.ApiKey);
        Assert.Equal("https://issuer.example.com", runtimeOpenAi.Issuer);
        Assert.Equal("openai-client", runtimeOpenAi.ClientId);
        Assert.Equal("api://openai/.default", runtimeOpenAi.Scopes);
        Assert.Equal("oidc-secret", runtimeOpenAi.ClientSecret);
        Assert.Equal("2025-01-01-preview", runtimeOpenAi.ApiVersion);
        Assert.Equal("openai", runtimeStore.Current.DefaultProvider);
        Assert.Equal("gpt-4o", runtimeStore.Current.DefaultModel);

        var savedJson = await keyVaultStore.GetSecretValueAsync("LLM--Models--openai", CancellationToken.None);
        var saved = JsonNode.Parse(savedJson!)!.AsObject();
        Assert.Equal("oidc", saved["authType"]?.GetValue<string>());
        Assert.Equal("", saved["apiKey"]?.GetValue<string>());
        Assert.Equal("https://issuer.example.com", saved["oidcIssuer"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteAsync_LlmDefault_PersistsDefaultLlmViaMountedAgentMcp()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gnougo-agent-providers-{Guid.NewGuid():N}.db");
        var agentMcpPort = GetFreePort();
        var agentMcpAddress = $"http://127.0.0.1:{agentMcpPort}";
        var app = AgentMcpWebHost.Build([
            $"--Agent:DatabasePath={dbPath}",
            $"--Kestrel:Endpoints:Http:Url={agentMcpAddress}"
        ], urls: agentMcpAddress);

        try
        {
            await app.StartAsync();

            var llm = new RecordingLlmClient();
            var modelCatalog = new FakeModelCatalog()
                .Add("ollama",
                    new LLMModelDescriptor("llama3", "llama3", "ollama", "ollama"),
                    new LLMModelDescriptor("llama3:8b", "llama3:8b", "ollama", "ollama"));
            var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
                .AddSecret("LLM--Models--ollama", "{\"provider\":\"ollama\",\"url\":\"http://localhost:11434\",\"model\":\"llama3\",\"authType\":\"none\"}");
            var humanInput = new AgentHumanInputProvider();
            var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
                },
                ModelOverrides = TestModelOverrides("llama3:8b"),
                McpServers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [AgentMcpHostingExtensions.ServerName] = new()
                    {
                        Type = "http",
                        Url = $"{agentMcpAddress}/mcp",
                        Description = "Test Agent MCP"
                    }
                }
            });
            var userConfigClient = new AgentUserConfigMcpClient(runtimeStore, NullLogger<AgentUserConfigMcpClient>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = cts.Token;
            var responder = Task.Run(async () =>
            {
                await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
                {
                    JsonNode response = request.StepId switch
                    {
                        "llm_default.provider" => new JsonObject { ["provider"] = "ollama" },
                        "llm_model.select.ollama" => new JsonObject { ["model"] = "llama3:8b" },
                        "llm_default.confirm_save" => new JsonObject { ["response"] = "save" },
                        _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                    };

                    humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                    if (request.StepId == "llm_default.confirm_save")
                        break;
                }
            }, token);

            var service = new ConfigureProvidersService(
                llm,
                humanInput,
                modelCatalog,
                keyVaultStore,
                runtimeStore,
                SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
                NullLogger<ConfigureProvidersService>.Instance,
                userConfigClient);

            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm default", token), token);
            await responder;

            Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ Default LLM provider set to 'ollama' with model 'llama3:8b'.");

            var config = await AgentMcpTestPersistence.GetUserConfigAsync(dbPath, token);
            Assert.Equal("ollama", config.DefaultLlmProvider);
            Assert.Equal("llama3:8b", config.DefaultLlmModel);
            Assert.Null(config.DefaultAgent);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task ExecuteAsync_McpList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--McpServers--github", "{}", 4, "2026-04-01T12:15:00+00:00")
            .AddSecret("LLM--McpServers--slack", "{}", 2, "2026-04-01T12:16:00+00:00")
            .AddSecret("LLM--Models--openai", "{}", 1, "2026-03-26T17:16:58.7030088+00:00");

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🔌 Configured MCP Servers", answer.Text);
        Assert.Contains("| github | `LLM--McpServers--github` | 4 |", answer.Text);
        Assert.Contains("| slack | `LLM--McpServers--slack` | 2 |", answer.Text);
        Assert.DoesNotContain("LLM--Models--openai", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmbeddingList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Embeddings--openai-small", "{\"provider\":\"openai\",\"name\":\"openai-small\",\"model\":\"text-embedding-3-small\",\"endpointUrl\":\"https://api.openai.com/v1\",\"dimensions\":1536}", 2, "2026-04-01T12:15:00+00:00")
            .AddSecret("LLM--Models--openai", "{}", 1, "2026-03-26T17:16:58.7030088+00:00");

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/embedding list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🧬 Configured Embedding Models", answer.Text);
        Assert.Contains("| openai-small |", answer.Text);
        Assert.Contains("`LLM--Embeddings--openai-small`", answer.Text);
        Assert.DoesNotContain("LLM--Models--openai", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Status_ReturnsCombinedStatusWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--openai", "{}", 2, "2026-04-01T12:10:00+00:00")
            .AddSecret("LLM--McpServers--github", "{}", 5, "2026-04-01T12:11:00+00:00");

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/status", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 📊 Current Configuration Status", answer.Text);
        Assert.Contains("## 🤖 LLM Providers", answer.Text);
        Assert.Contains("## 🔌 MCP Servers", answer.Text);
        Assert.Contains("`LLM--Models--openai`", answer.Text);
        Assert.Contains("`LLM--McpServers--github`", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmModels_ReturnsAvailableModelsWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"),
                new LLMModelDescriptor("gpt-4o-mini", "gpt-4o-mini", "openai", "openai"));

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                }
            });

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm models openai", CancellationToken.None));

        Assert.Equal(2, events.Count);
        Assert.Equal("thinking:thinking", events[0].Type);
        var answer = events[1];
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🧠 Available Models for `openai`", answer.Text);
        Assert.Contains("`gpt-4o`", answer.Text);
        Assert.Contains("`gpt-4o-mini`", answer.Text);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(1, modelCatalog.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmModels_EmitsLoadingEventBeforeFinalAnswer()
    {
        var llm = new RecordingLlmClient();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"));

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                }
            });

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm models openai", CancellationToken.None));

        Assert.Equal(2, events.Count);
        Assert.Equal("thinking:thinking", events[0].Type);
        Assert.Contains("Loading live model catalog", events[0].Text);
        Assert.Equal("answer", events[1].Type);
        Assert.Contains("# 🧠 Available Models for `openai`", events[1].Text);
        Assert.Contains("`gpt-4o`", events[1].Text);
        Assert.Equal(1, modelCatalog.CallCount);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmModels_RendersConciseDiscoveryError()
    {
        var llm = new RecordingLlmClient();
        var modelCatalog = new ThrowingModelCatalog(new AggregateException(
            new InvalidOperationException("Model availability probe failed with 401 Unauthorized - Unauthorized"),
            new InvalidOperationException("Model availability probe failed with 401 Unauthorized - Unauthorized"),
            new InvalidOperationException("Model availability probe was rate-limited: Too many requests.")));

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "copilot",
                DefaultModel = "gpt-4o",
                Models = new Dictionary<string, ModelProviderOptions>
                {
                    ["copilot"] = new() { Url = "https://models.github.ai/inference", Type = "copilot" }
                }
            });

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm models copilot", CancellationToken.None));

        Assert.Equal(2, events.Count);
        var answer = events[1];
        Assert.Equal("answer", answer.Type);
        Assert.Contains("❌ Live model discovery failed.", answer.Text);
        Assert.Equal(1, TestStringHelpers.CountOccurrences(answer.Text ?? string.Empty, "401 Unauthorized"));
        Assert.Equal(1, TestStringHelpers.CountOccurrences(answer.Text ?? string.Empty, "Too many requests"));
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ListModelsEndpoint_ReturnsConfiguredProviderModels()
    {
        var options = new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>
            {
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
            }
        };

        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        var catalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"));

        var result = await LlmProviderEndpoints.ListModelsAsync("openai", catalog, store, CancellationToken.None);

        var ok = Assert.IsType<Ok<LlmProviderModelsDto>>(result);
        var payload = Assert.IsType<LlmProviderModelsDto>(ok.Value);
        Assert.Equal("openai", payload.Provider);
        Assert.Single(payload.Models);
        Assert.Equal("gpt-4o", payload.Models[0].Id);
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_UsesInteractiveModelSelectionWhenDiscoverySucceeds()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();

        var modelCatalog = new FakeModelCatalog()
            .Add("ollama",
                new LLMModelDescriptor("llama3", "llama3", "local", "ollama"),
                new LLMModelDescriptor("llama3:8b", "llama3:8b", "local", "ollama"));

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "ollama" },
                    "llm_add.connection" => new JsonObject { ["url"] = "http://localhost:11434" },
                    "llm_model.select.ollama" => new JsonObject { ["model"] = "llama3:8b" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "ollama",
                DefaultModel = "llama3",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase),
                ModelOverrides = TestModelOverrides("llama3:8b")
            },
            humanInput,
            keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("llm_model.select.ollama", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("llm_model.manual.ollama", StringComparison.Ordinal) == true);

        var configJson = await keyVaultStore.GetSecretValueAsync("LLM--Models--ollama", CancellationToken.None);
        Assert.NotNull(configJson);
        var config = JsonNode.Parse(configJson)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("llama3:8b", config["model"]?.GetValue<string>());
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_WhenFirstProviderIsCreated_SetsItAsDefault()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-4o-mini", "gpt-4o-mini", "openai", "openai"));

        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = string.Empty,
            DefaultModel = string.Empty,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "openai" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "api_key" },
                    "llm.auth.api_key" => new JsonObject { ["api_key"] = "test-secret" },
                    "llm_model.select.openai" => new JsonObject { ["model"] = "gpt-4o-mini" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            modelCatalog,
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Equal("openai", runtimeStore.Current.DefaultProvider);
        Assert.Equal("gpt-4o-mini", runtimeStore.Current.DefaultModel);
        Assert.Contains(events, evt => evt.Type == "thinking:response" && evt.Text == "⭐ Provider 'openai' was set as the default LLM with model 'gpt-4o-mini'.");
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_CanConfigureOidcWithPrivateKeyPem()
    {
        const string privateKeyPem = "-----BEGIN PRIVATE KEY-----\nMIIBVwIBADANBgkqhkiG9w0BAQEFAASCAT8wggE7AgEAAkEArandomtestkey\n-----END PRIVATE KEY-----";

        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-4o-mini", "gpt-4o-mini", "openai", "openai"));

        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = string.Empty,
            DefaultModel = string.Empty,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "openai" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "oidc" },
                    "llm.auth.oidc" => new JsonObject
                    {
                        ["issuer"] = "https://issuer.example",
                        ["client_id"] = "client-id",
                        ["scopes"] = "models.read",
                        ["client_secret"] = string.Empty,
                        ["private_key_pem"] = privateKeyPem,
                        ["api_version"] = "2025-01-01-preview"
                    },
                    "llm_model.select.openai" => new JsonObject { ["model"] = "gpt-4o-mini" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            modelCatalog,
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        var configJson = await keyVaultStore.GetSecretValueAsync("LLM--Models--openai", CancellationToken.None);
        Assert.NotNull(configJson);

        var config = JsonNode.Parse(configJson!)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("oidc", config["authType"]?.GetValue<string>());
        Assert.Equal("https://issuer.example", config["oidcIssuer"]?.GetValue<string>());
        Assert.Equal("client-id", config["oidcClientId"]?.GetValue<string>());
        Assert.Equal("models.read", config["oidcScopes"]?.GetValue<string>());
        Assert.Equal(privateKeyPem, config["oidcPrivateKeyPem"]?.GetValue<string>());
        Assert.Equal("", config["oidcClientSecret"]?.GetValue<string>());

        Assert.True(runtimeStore.Current.Models.TryGetValue("openai", out var runtimeProvider));
        Assert.NotNull(runtimeProvider);
        Assert.Equal("https://issuer.example", runtimeProvider.Issuer);
        Assert.Equal("client-id", runtimeProvider.ClientId);
        Assert.Equal("models.read", runtimeProvider.Scopes);
        Assert.Equal(privateKeyPem, runtimeProvider.PrivateKeyPem);
        Assert.Null(runtimeProvider.ClientSecret);
        Assert.Equal("2025-01-01-preview", runtimeProvider.ApiVersion);
        Assert.Equal("openai", runtimeStore.Current.DefaultProvider);
        Assert.Equal("gpt-4o-mini", runtimeStore.Current.DefaultModel);

        Assert.Contains(events, evt => evt.Type == "thinking:response" && evt.Text == "✅ Credentials validated. Provider 'openai' is ready.");
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_CanConfigureClaudeProvider()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var modelCatalog = new FakeModelCatalog()
            .Add("anthropic",
                new LLMModelDescriptor("claude-sonnet-4-20250514", "Claude Sonnet 4", "anthropic", "anthropic"));

        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = string.Empty,
            DefaultModel = string.Empty,
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "anthropic" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.anthropic.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "api_key" },
                    "llm.auth.api_key" => new JsonObject { ["api_key"] = "sk-ant-secret" },
                    "llm_model.select.anthropic" => new JsonObject { ["model"] = "claude-sonnet-4-20250514" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            modelCatalog,
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        var configJson = await keyVaultStore.GetSecretValueAsync("LLM--Models--anthropic", CancellationToken.None);
        Assert.NotNull(configJson);
        var config = JsonNode.Parse(configJson)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("anthropic", config["provider"]?.GetValue<string>());
        Assert.Equal("anthropic", config["type"]?.GetValue<string>());
        Assert.Equal("https://api.anthropic.com/v1", config["url"]?.GetValue<string>());
        Assert.Equal("claude-sonnet-4-20250514", config["model"]?.GetValue<string>());
        Assert.Equal("api_key", config["authType"]?.GetValue<string>());
        Assert.Equal("sk-ant-secret", config["apiKey"]?.GetValue<string>());

        Assert.True(runtimeStore.Current.Models.TryGetValue("anthropic", out var runtimeProvider));
        Assert.NotNull(runtimeProvider);
        Assert.Equal("anthropic", runtimeProvider.Type);
        Assert.Equal("anthropic", runtimeProvider.ResolvedType);
        Assert.Equal("anthropic", runtimeStore.Current.DefaultProvider);
        Assert.Equal("claude-sonnet-4-20250514", runtimeStore.Current.DefaultModel);

        Assert.Equal(1, llm.CallCount);
        Assert.NotNull(llm.LastRequest);
        Assert.Equal("anthropic", llm.LastRequest!.Provider);
        Assert.Equal("claude-sonnet-4-20250514", llm.LastRequest.Model);
        Assert.Contains(events, evt => evt.Type == "thinking:response" && evt.Text == "✅ Credentials validated. Provider 'anthropic' is ready.");
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_WhenOverwritingExistingProviderAndDefaultIsStale_RepairsDefaultSelection()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_llm_openai", "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"gpt-4o-mini\",\"auth_type\":\"api_key\",\"api_key\":\"old-secret\"}");
        var modelCatalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-5-search-api", "gpt-5-search-api", "openai", "openai"));

        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "copilot",
            DefaultModel = "gpt-4o",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["copilot"] = new() { Url = "https://models.github.ai/inference", Type = "copilot" },
                ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "runtime-secret" }
                },
                ModelOverrides = TestModelOverrides("gpt-5-search-api")
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "openai" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "api_key" },
                    "llm.auth.api_key" => new JsonObject { ["api_key"] = "new-secret" },
                    "llm_model.select.openai" => new JsonObject { ["model"] = "gpt-5-search-api" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = new ConfigureProvidersService(
            llm,
            humanInput,
            modelCatalog,
            keyVaultStore,
            runtimeStore,
            SmartFlowTestFactory.CreateTelemetryHarness().Telemetry,
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Equal("openai", runtimeStore.Current.DefaultProvider);
        Assert.Equal("gpt-5-search-api", runtimeStore.Current.DefaultModel);
        Assert.Contains(events, evt => evt.Type == "thinking:response" && evt.Text == "⭐ Provider 'openai' was set as the default LLM with model 'gpt-5-search-api'.");
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_ValidationOmitsTemperatureForSavedProvider()
    {   
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai",
                new LLMModelDescriptor("gpt-5-search-api", "gpt-5-search-api", "openai", "openai"));

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "openai" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "api_key" },
                    "llm.auth.api_key" => new JsonObject { ["api_key"] = "test-secret" },
                    "llm_model.select.openai" => new JsonObject { ["model"] = "gpt-5-search-api" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase),
                ModelOverrides = TestModelOverrides("gpt-5-search-api")
            },
            humanInput,
            keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Equal(1, llm.CallCount);
        Assert.NotNull(llm.LastRequest);
        Assert.Equal("openai", llm.LastRequest!.Provider);
        Assert.Equal("gpt-5-search-api", llm.LastRequest.Model);
        Assert.Null(llm.LastRequest.Temperature);
        Assert.Contains(events, evt => evt.Type == "thinking:response" && evt.Text == "✅ Credentials validated. Provider 'openai' is ready.");
    }

    [Fact]
    public async Task ExecuteAsync_LlmRemove_DeletesSecretWithoutKeyVaultMcpCrud()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_llm_openai", "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"gpt-4o-mini\",\"auth_type\":\"api_key\",\"api_key\":\"secret\"}");
        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                if (request.StepId == "llm_remove.confirm")
                {
                    humanInput.TrySubmitResponse(request.RunId, request.StepId, new JsonObject { ["response"] = "confirm" });
                    break;
                }
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            options: new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                }
            },
            humanInput: humanInput,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm remove openai", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ LLM provider 'openai' removed.");
        Assert.Null(await keyVaultStore.GetSecretValueAsync("gnougo_llm_openai", CancellationToken.None));
        Assert.Null(await keyVaultStore.GetSecretValueAsync("LLM--Models--openai", CancellationToken.None));
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_McpAdd_SavesSecretThroughDirectKeyVaultStore()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "mcp_add.transport" => new JsonObject { ["response"] = "stdio" },
                    "mcp_add.stdio" => new JsonObject
                    {
                        ["name"] = "GithubLocal",
                        ["description"] = "Local GitHub MCP",
                        ["command"] = "dotnet",
                        ["args"] = "run,--project,src/GnOuGo.Agent.Mcp/GnOuGo.Agent.Mcp.csproj"
                    },
                    "mcp_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "mcp_add.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            humanInput: humanInput,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp add", token), token);
        await responder;

        var savedJson = await keyVaultStore.GetSecretValueAsync("LLM--McpServers--GithubLocal", CancellationToken.None);
        Assert.NotNull(savedJson);
        var saved = JsonNode.Parse(savedJson)?.AsObject();
        Assert.NotNull(saved);
        Assert.Equal("stdio", saved["transport"]?.GetValue<string>());
        Assert.Equal("dotnet", saved["command"]?.GetValue<string>());
        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ MCP server 'GithubLocal' saved to KeyVault.");
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_McpEdit_UpdatesSecretAndPreservesExistingAuth()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_mcp_Github", "{\"name\":\"Github\",\"transport\":\"http\",\"description\":\"Old description\",\"url\":\"https://old.example/mcp\",\"auth_type\":\"api_key\",\"api_key\":\"gh-secret\"}");
        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "mcp_edit.http" => new JsonObject
                    {
                        ["description"] = "Updated description",
                        ["url"] = "https://api.githubcopilot.com/mcp/"
                    },
                    "mcp_edit.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "mcp_edit.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            humanInput: humanInput,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp edit Github", token), token);
        await responder;

        var savedJson = await keyVaultStore.GetSecretValueAsync("LLM--McpServers--Github", CancellationToken.None);
        Assert.NotNull(savedJson);
        var saved = JsonNode.Parse(savedJson)?.AsObject();
        Assert.NotNull(saved);
        Assert.Equal("Updated description", saved["description"]?.GetValue<string>());
        Assert.Equal("https://api.githubcopilot.com/mcp/", saved["url"]?.GetValue<string>());
        Assert.Equal("api_key", saved["authType"]?.GetValue<string>());
        Assert.Equal("gh-secret", saved["apiKey"]?.GetValue<string>());
        Assert.Null(await keyVaultStore.GetSecretValueAsync("gnougo_mcp_Github", CancellationToken.None));
        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ MCP server 'Github' updated.");
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_McpRemove_DeletesSecretWithoutKeyVaultMcpCrud()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_mcp_Github", "{\"name\":\"Github\",\"transport\":\"http\",\"description\":\"GitHub\",\"url\":\"https://api.githubcopilot.com/mcp/\"}");
        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                if (request.StepId == "mcp_remove.confirm")
                {
                    humanInput.TrySubmitResponse(request.RunId, request.StepId, new JsonObject { ["response"] = "confirm" });
                    break;
                }
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            humanInput: humanInput,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp remove Github", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ MCP server 'Github' removed.");
        Assert.Null(await keyVaultStore.GetSecretValueAsync("gnougo_mcp_Github", CancellationToken.None));
        Assert.Null(await keyVaultStore.GetSecretValueAsync("LLM--McpServers--Github", CancellationToken.None));
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Status_PersistsDirectCommandSpansUnderCurrentChatTrace()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--openai", "{}", 2, "2026-04-01T12:10:00+00:00")
            .AddSecret("LLM--McpServers--github", "{}", 5, "2026-04-01T12:11:00+00:00");
        var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore,
            telemetry: telemetryHarness.Telemetry);

        var correlationId = ActivityTraceId.CreateRandom().ToHexString();
        using (var chatTrace = telemetryHarness.Telemetry.StartChatMessageActivity(correlationId, "/status"))
        {
            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/status", CancellationToken.None));
            Assert.Single(events);
            chatTrace.SetStatus(ActivityStatusCode.Ok);
        }

        var spans = DrainPersistedSpans(telemetryHarness.Queue);
        Assert.True(spans.Count >= 3);

        var traceIds = spans.Select(span => Convert.ToHexString(span.TraceId).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        Assert.Single(traceIds);
        Assert.Contains(spans, span => span.Name == "configure.providers.status");
        Assert.Contains(spans, span => span.Name == "configure.providers.status.render");
        Assert.Contains(spans, span => span.Name == "keyvault.list_secrets");
    }

    [Fact]
    public async Task ExecuteAsync_LlmModels_PersistsModelCatalogDiscoverySpansUnderCurrentChatTrace()
    {
        var llm = new RecordingLlmClient();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai", new LLMModelDescriptor("gpt-4o", "gpt-4o", "openai", "openai"));
        var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o",
                Models = new Dictionary<string, ModelProviderOptions>
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                }
            },
            telemetry: telemetryHarness.Telemetry);

        var correlationId = ActivityTraceId.CreateRandom().ToHexString();
        using (var chatTrace = telemetryHarness.Telemetry.StartChatMessageActivity(correlationId, "/llm models openai"))
        {
            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm models openai", CancellationToken.None));
            Assert.Equal(2, events.Count);
            chatTrace.SetStatus(ActivityStatusCode.Ok);
        }

        var spans = DrainPersistedSpans(telemetryHarness.Queue);
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.models");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.models.render");
        Assert.Contains(spans, span => span.Name == "llm.model_catalog.list");
    }

    [Fact]
    public async Task ExecuteAsync_LlmAdd_PersistsInteractiveWizardSpansUnderCurrentChatTrace()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var modelCatalog = new FakeModelCatalog()
            .Add("openai", new LLMModelDescriptor("gpt-4o-mini", "gpt-4o-mini", "openai", "openai"));
        var humanInput = new AgentHumanInputProvider();
        var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_add.provider" => new JsonObject { ["response"] = "openai" },
                    "llm_add.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_add.auth_mode" => new JsonObject { ["response"] = "api_key" },
                    "llm.auth.api_key" => new JsonObject { ["api_key"] = "test-secret" },
                    "llm_model.select.openai" => new JsonObject { ["model"] = "gpt-4o-mini" },
                    "llm_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_add.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = string.Empty,
                DefaultModel = string.Empty,
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            },
            humanInput,
            keyVaultStore,
            telemetryHarness.Telemetry);

        var correlationId = ActivityTraceId.CreateRandom().ToHexString();
        using (var chatTrace = telemetryHarness.Telemetry.StartChatMessageActivity(correlationId, "/llm add"))
        {
            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
            Assert.NotEmpty(events);
            chatTrace.SetStatus(ActivityStatusCode.Ok);
        }

        await responder;

        var spans = DrainPersistedSpans(telemetryHarness.Queue);
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.add.interactive");
        Assert.Contains(spans, span => span.Name == "configure.providers.human_input.request");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.auth.collect");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.model.select");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.save");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.validate");
    }

    [Fact]
    public async Task ExecuteAsync_LlmEdit_PersistsInteractiveWizardSpansUnderCurrentChatTrace()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--Models--openai", "{\"provider\":\"openai\",\"url\":\"https://api.openai.com/v1\",\"model\":\"gpt-4o-mini\",\"authType\":\"api_key\",\"apiKey\":\"old-secret\"}");
        var modelCatalog = new FakeModelCatalog()
            .Add("openai", new LLMModelDescriptor("gpt-5-search-api", "gpt-5-search-api", "openai", "openai"));
        var humanInput = new AgentHumanInputProvider();
        var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "llm_edit.connection" => new JsonObject { ["url"] = "https://api.openai.com/v1" },
                    "llm_edit.auth_mode" => new JsonObject { ["response"] = "api_key" },
                    "llm.auth.api_key" => new JsonObject { ["api_key"] = "new-secret" },
                    "llm_model.select.openai" => new JsonObject { ["model"] = "gpt-5-search-api" },
                    "llm_edit.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "llm_edit.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "runtime-secret" }
                },
                ModelOverrides = TestModelOverrides("gpt-5-search-api")
            },
            humanInput,
            keyVaultStore,
            telemetry: telemetryHarness.Telemetry);

        var correlationId = ActivityTraceId.CreateRandom().ToHexString();
        using (var chatTrace = telemetryHarness.Telemetry.StartChatMessageActivity(correlationId, "/llm edit openai"))
        {
            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm edit openai", token), token);
            Assert.NotEmpty(events);
            chatTrace.SetStatus(ActivityStatusCode.Ok);
        }

        await responder;

        var spans = DrainPersistedSpans(telemetryHarness.Queue);
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.edit.interactive");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.load_existing");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.auth.collect");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.model.select");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.save");
        Assert.Contains(spans, span => span.Name == "configure.providers.llm.validate");
    }

    [Fact]
    public async Task ExecuteAsync_McpAdd_PersistsInteractiveWizardSpansUnderCurrentChatTrace()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore();
        var humanInput = new AgentHumanInputProvider();
        var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "mcp_add.transport" => new JsonObject { ["response"] = "stdio" },
                    "mcp_add.stdio" => new JsonObject
                    {
                        ["name"] = "GithubLocal",
                        ["description"] = "Local GitHub MCP",
                        ["command"] = "dotnet",
                        ["args"] = "run,--project,src/GnOuGo.Agent.Mcp/GnOuGo.Agent.Mcp.csproj"
                    },
                    "mcp_add.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "mcp_add.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            humanInput: humanInput,
            keyVaultStore: keyVaultStore,
            telemetry: telemetryHarness.Telemetry);

        var correlationId = ActivityTraceId.CreateRandom().ToHexString();
        using (var chatTrace = telemetryHarness.Telemetry.StartChatMessageActivity(correlationId, "/mcp add"))
        {
            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp add", token), token);
            Assert.NotEmpty(events);
            chatTrace.SetStatus(ActivityStatusCode.Ok);
        }

        await responder;

        var spans = DrainPersistedSpans(telemetryHarness.Queue);
        Assert.Contains(spans, span => span.Name == "configure.providers.mcp.add.interactive");
        Assert.Contains(spans, span => span.Name == "configure.providers.human_input.request");
        Assert.Contains(spans, span => span.Name == "configure.providers.mcp.stdio.collect");
        Assert.Contains(spans, span => span.Name == "configure.providers.mcp.save");
    }

    [Fact]
    public async Task ExecuteAsync_McpEdit_PersistsInteractiveWizardSpansUnderCurrentChatTrace()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("LLM--McpServers--Github", "{\"name\":\"Github\",\"transport\":\"http\",\"description\":\"Old description\",\"url\":\"https://old.example/mcp\",\"authType\":\"api_key\",\"apiKey\":\"gh-secret\"}");
        var humanInput = new AgentHumanInputProvider();
        var telemetryHarness = SmartFlowTestFactory.CreateTelemetryHarness();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(token))
            {
                JsonNode response = request.StepId switch
                {
                    "mcp_edit.http" => new JsonObject
                    {
                        ["description"] = "Updated description",
                        ["url"] = "https://api.githubcopilot.com/mcp/"
                    },
                    "mcp_edit.confirm_save" => new JsonObject { ["response"] = "save" },
                    _ => throw new InvalidOperationException($"Unexpected step id: {request.StepId}")
                };

                humanInput.TrySubmitResponse(request.RunId, request.StepId, response);

                if (request.StepId == "mcp_edit.confirm_save")
                    break;
            }
        }, token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            humanInput: humanInput,
            keyVaultStore: keyVaultStore,
            telemetry: telemetryHarness.Telemetry);

        var correlationId = ActivityTraceId.CreateRandom().ToHexString();
        using (var chatTrace = telemetryHarness.Telemetry.StartChatMessageActivity(correlationId, "/mcp edit Github"))
        {
            var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp edit Github", token), token);
            Assert.NotEmpty(events);
            chatTrace.SetStatus(ActivityStatusCode.Ok);
        }

        await responder;

        var spans = DrainPersistedSpans(telemetryHarness.Queue);
        Assert.Contains(spans, span => span.Name == "configure.providers.mcp.edit.interactive");
        Assert.Contains(spans, span => span.Name == "configure.providers.mcp.load_existing");
        Assert.Contains(spans, span => span.Name == "configure.providers.human_input.request");
        Assert.Contains(spans, span => span.Name == "configure.providers.mcp.save");
    }

    private static List<SpanRow> DrainPersistedSpans(OtlpTenantCollector.Services.TelemetryIngestQueue queue)
    {
        var spans = new List<SpanRow>();
        while (queue.Channel.Reader.TryRead(out var row))
        {
            if (row is SpanRow span)
                spans.Add(span);
        }

        return spans;
    }

    private static Dictionary<string, LLMModelMetadata> TestModelOverrides(params string[] modelIds)
    {
        var result = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var modelId in modelIds)
        {
            var isGpt5 = modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
            result[modelId] = new LLMModelMetadata
            {
                Id = modelId,
                ProviderType = isGpt5 ? "openai" : "ollama",
                DisplayName = modelId,
                OwnedBy = isGpt5 ? "openai" : "local",
                ContextWindowTokens = isGpt5 ? 400000 : 32768,
                MaxInputTokens = isGpt5 ? 400000 : 32768,
                MaxOutputTokens = isGpt5 ? 128000 : 4096,
                Pricing = new ModelPricingMetadata
                {
                    InputPer1MTokens = isGpt5 ? 75m : 0m,
                    OutputPer1MTokens = isGpt5 ? 150m : 0m
                },
                Capabilities = new ModelCapabilityMetadata
                {
                    SupportsTemperature = !isGpt5,
                    SupportsReasoningEffort = isGpt5,
                    SupportsStructuredOutput = true,
                    SupportsTools = true,
                    SupportsJsonMode = true,
                    UnsupportedRequestParameters = isGpt5 ? ["temperature"] : null
                }
            };
        }

        return result;
    }
}

file static class ConfigureProvidersServiceTestHelpers
{
    public static bool InvokeShouldUseInjectedModelCatalog(
        ConfigureProvidersService service,
        string provider,
        ModelProviderOptions providerOptions)
    {
        var method = typeof(ConfigureProvidersService).GetMethod(
            "ShouldUseInjectedModelCatalog",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var value = method.Invoke(service, [provider, providerOptions]);
        Assert.NotNull(value);
        return (bool)value;
    }
}

file sealed class ThrowingModelCatalog(Exception error) : ILLMModelCatalog
{
    public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(string provider, CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<LLMModelDescriptor>>(error);
}

file static class TestStringHelpers
{
    public static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

