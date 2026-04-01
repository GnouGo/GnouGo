using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Server.Endpoints;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Shared;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ConfigureProvidersServiceTests
{
    [Fact]
    public async Task ExecuteAsync_LlmList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVault = new FakeMcpSession("GnOuGo.KeyVault.Mcp")
            .OnTool("keyvault_list_secrets", SmartFlowTestFactory.KeyVaultListSecretsResult(
                ("gnougo_llm_copilot", 3, "2026-04-01T12:13:24.4824726+00:00"),
                ("gnougo_llm_openai", 1, "2026-03-26T17:16:58.7030088+00:00"),
                ("gnougo_mcp_github", 2, "2026-04-01T12:15:00+00:00")));

        var service = SmartFlowTestFactory.CreateProvidersService(llm, new FakeMcpClientFactory(keyVault));

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🤖 Configured LLM Providers", answer.Text);
        Assert.Contains("| copilot | `gnougo_llm_copilot` | 3 |", answer.Text);
        Assert.Contains("| openai | `gnougo_llm_openai` | 1 |", answer.Text);
        Assert.DoesNotContain("gnougo_mcp_github", answer.Text, StringComparison.Ordinal);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_McpList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVault = new FakeMcpSession("GnOuGo.KeyVault.Mcp")
            .OnTool("keyvault_list_secrets", SmartFlowTestFactory.KeyVaultListSecretsResult(
                ("gnougo_mcp_github", 4, "2026-04-01T12:15:00+00:00"),
                ("gnougo_mcp_slack", 2, "2026-04-01T12:16:00+00:00"),
                ("gnougo_llm_openai", 1, "2026-03-26T17:16:58.7030088+00:00")));

        var service = SmartFlowTestFactory.CreateProvidersService(llm, new FakeMcpClientFactory(keyVault));

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
        var keyVault = new FakeMcpSession("GnOuGo.KeyVault.Mcp")
            .OnTool("keyvault_list_secrets", SmartFlowTestFactory.KeyVaultListSecretsResult(
                ("gnougo_llm_openai", 2, "2026-04-01T12:10:00+00:00"),
                ("gnougo_mcp_github", 5, "2026-04-01T12:11:00+00:00")));

        var service = SmartFlowTestFactory.CreateProvidersService(llm, new FakeMcpClientFactory(keyVault));

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
            new FakeMcpClientFactory(new FakeMcpSession("GnOuGo.KeyVault.Mcp")),
            modelCatalog,
            new GnOuGo.AI.Core.LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>
                {
                    ["openai"] = new() { Url = "https://api.openai.com/v1", Type = "openai", ApiKey = "secret" }
                }
            });

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm models openai", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🧠 Available Models for `openai`", answer.Text);
        Assert.Contains("`gpt-4o`", answer.Text);
        Assert.Contains("`gpt-4o-mini`", answer.Text);
        Assert.Equal(0, llm.CallCount);
        Assert.Equal(1, modelCatalog.CallCount);
    }

    [Fact]
    public async Task ListModelsEndpoint_ReturnsConfiguredProviderModels()
    {
        var options = new GnOuGo.AI.Core.LLMOptions
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
        JsonNode? savedArguments = null;
        var keyVault = new FakeMcpSession("GnOuGo.KeyVault.Mcp")
            .OnTool("keyvault_set_secret", (arguments, _) =>
            {
                savedArguments = arguments?.DeepClone();
                return Task.FromResult(new McpCallResult
                {
                    IsError = false,
                    Content = new JsonObject { ["Success"] = true }
                });
            });

        var modelCatalog = new FakeModelCatalog()
            .Add("ollama",
                new LLMModelDescriptor("llama3", "llama3", "local", "ollama"),
                new LLMModelDescriptor("llama3:8b", "llama3:8b", "local", "ollama"));

        var humanInput = new AgentHumanInputProvider();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = Task.Run(async () =>
        {
            await foreach (var request in humanInput.PendingRequests.ReadAllAsync(cts.Token))
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
        }, cts.Token);

        var service = SmartFlowTestFactory.CreateProvidersService(
            llm,
            new FakeMcpClientFactory(keyVault),
            modelCatalog,
            new LLMOptions
            {
                DefaultProvider = "ollama",
                DefaultModel = "llama3",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            },
            humanInput);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/llm add", cts.Token), cts.Token);
        await responder;

        Assert.Contains(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("llm_model.select.ollama", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(events, evt => evt.Type == "human_input_request" && evt.Text?.Contains("llm_model.manual.ollama", StringComparison.Ordinal) == true);

        Assert.NotNull(savedArguments);
        var savedPayload = Assert.IsType<JsonObject>(savedArguments!);
        Assert.Equal("gnougo_llm_ollama", savedPayload["key"]?.GetValue<string>());

        var configJson = savedPayload["value"]?.GetValue<string>();
        Assert.NotNull(configJson);
        var config = JsonNode.Parse(configJson!)?.AsObject();
        Assert.NotNull(config);
        Assert.Equal("llama3:8b", config!["model"]?.GetValue<string>());
        Assert.Equal(0, llm.CallCount);
    }
}

