using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Endpoints;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Shared;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ConfigureProvidersServiceTests
{
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
            .AddSecret("gnougo_llm_copilot", "{}", 3, "2026-04-01T12:13:24.4824726+00:00")
            .AddSecret("gnougo_llm_openai", "{}", 1, "2026-03-26T17:16:58.7030088+00:00")
            .AddSecret("gnougo_mcp_github", "{}", 2, "2026-04-01T12:15:00+00:00");

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🤖 Configured LLM Providers", answer.Text);
        Assert.Contains("| Provider | Default | Model | Key | Version | Stored |", answer.Text);
        Assert.Contains("| openai | ✅ yes | gpt-4o-mini | `gnougo_llm_openai` | 1 |", answer.Text);
        Assert.Contains("| copilot |", answer.Text);
        Assert.Contains("`gnougo_llm_copilot`", answer.Text);
        Assert.DoesNotContain("gnougo_mcp_github", answer.Text, StringComparison.Ordinal);
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
            .AddSecret("gnougo_llm_ollama", "{\"provider\":\"ollama\",\"url\":\"http://localhost:11434\",\"model\":\"llama3\",\"auth_type\":\"none\"}");
        var humanInput = new AgentHumanInputProvider();
        var runtimeStore = SmartFlowTestFactory.CreateRuntimeOptionsStore(new LLMOptions
        {
            DefaultProvider = "openai",
            DefaultModel = "gpt-4o-mini",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
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
            NullLogger<ConfigureProvidersService>.Instance);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm default", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "answer" && evt.Text == "✅ Default LLM provider set to 'ollama' with model 'llama3:8b'.");
        Assert.Equal("ollama", runtimeStore.Current.DefaultProvider);
        Assert.Equal("llama3:8b", runtimeStore.Current.DefaultModel);

        var configJson = await keyVaultStore.GetSecretValueAsync("gnougo_llm_ollama", CancellationToken.None);
        var config = JsonNode.Parse(configJson!)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("llama3:8b", config!["model"]?.GetValue<string>());
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_McpList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_mcp_github", "{}", 4, "2026-04-01T12:15:00+00:00")
            .AddSecret("gnougo_mcp_slack", "{}", 2, "2026-04-01T12:16:00+00:00")
            .AddSecret("gnougo_llm_openai", "{}", 1, "2026-03-26T17:16:58.7030088+00:00");

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            keyVaultStore: keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/mcp list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🔌 Configured MCP Servers", answer.Text);
        Assert.Contains("| github | `gnougo_mcp_github` | 4 |", answer.Text);
        Assert.Contains("| slack | `gnougo_mcp_slack` | 2 |", answer.Text);
        Assert.DoesNotContain("gnougo_llm_openai", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_Status_ReturnsCombinedStatusWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_llm_openai", "{}", 2, "2026-04-01T12:10:00+00:00")
            .AddSecret("gnougo_mcp_github", "{}", 5, "2026-04-01T12:11:00+00:00");

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
        Assert.Contains("`gnougo_llm_openai`", answer.Text);
        Assert.Contains("`gnougo_mcp_github`", answer.Text);
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
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            },
            humanInput,
            keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", token), token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("llm_model.select.ollama", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("llm_model.manual.ollama", StringComparison.Ordinal) == true);

        var configJson = await keyVaultStore.GetSecretValueAsync("gnougo_llm_ollama", CancellationToken.None);
        Assert.NotNull(configJson);
        var config = JsonNode.Parse(configJson)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("llama3:8b", config["model"]?.GetValue<string>());
        Assert.Equal(0, llm.CallCount);
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

        var savedJson = await keyVaultStore.GetSecretValueAsync("gnougo_mcp_GithubLocal", CancellationToken.None);
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

        var savedJson = await keyVaultStore.GetSecretValueAsync("gnougo_mcp_Github", CancellationToken.None);
        Assert.NotNull(savedJson);
        var saved = JsonNode.Parse(savedJson)?.AsObject();
        Assert.NotNull(saved);
        Assert.Equal("Updated description", saved["description"]?.GetValue<string>());
        Assert.Equal("https://api.githubcopilot.com/mcp/", saved["url"]?.GetValue<string>());
        Assert.Equal("api_key", saved["auth_type"]?.GetValue<string>());
        Assert.Equal("gh-secret", saved["api_key"]?.GetValue<string>());
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
        Assert.Equal(0, llm.CallCount);
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

