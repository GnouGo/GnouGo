using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ConfigureAgentsServiceTests
{
    [Fact]
    public async Task ExecuteAsync_AgentList_ReturnsDeterministicMarkdownWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var agentMcp = new FakeMcpSession("GnOuGo.Agent.Mcp")
            .OnTool("agent_list", SmartFlowTestFactory.AgentListResult(
                SmartFlowTestFactory.AgentSummary(
                    "12345678-1234-1234-1234-1234567890ab",
                    "daily-reporter",
                    2,
                    "2026-04-01T12:30:00+00:00"),
                SmartFlowTestFactory.AgentSummary(
                    "87654321-4321-4321-4321-ba0987654321",
                    "reviewer",
                    0,
                    "2026-04-01T12:35:00+00:00")));

        var service = SmartFlowTestFactory.CreateAgentsService(llm, new FakeMcpClientFactory(agentMcp));

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent list", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.NotNull(answer.Text);
        Assert.Contains("# 🤖 Configured Agents", answer.Text);
        Assert.Contains("| daily-reporter | `12345678` | 2 |", answer.Text);
        Assert.Contains("| reviewer | `87654321` | 0 |", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentAdd_WithoutConfiguredProvider_ReturnsGuidanceWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVault = new FakeMcpSession("GnOuGo.KeyVault.Mcp")
            .OnTool("keyvault_list_secrets", SmartFlowTestFactory.KeyVaultListSecretsResult(
                ("gnougo_mcp_github", 1, "2026-04-01T12:00:00+00:00")));

        var service = SmartFlowTestFactory.CreateAgentsService(
            llm,
            new FakeMcpClientFactory(keyVault),
            new LLMOptions
            {
                DefaultProvider = "OpenAi",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>()
            });

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent add", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Equal("❌ Configure a default LLM provider first. Use `/llm add` to create one, then `/llm default` before retrying `/agent add`.", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AgentAdd_WithoutConfiguredDefaultProvider_ReturnsGuidanceWithoutCallingLlm()
    {
        var llm = new RecordingLlmClient();
        var keyVaultStore = new FakeKeyVaultRuntimeConfigStore()
            .AddSecret("gnougo_llm_ollama", "{\"provider\":\"ollama\",\"url\":\"http://localhost:11434\",\"model\":\"llama3\",\"auth_type\":\"none\"}");

        var service = SmartFlowTestFactory.CreateAgentsService(
            llm,
            new FakeMcpClientFactory(new FakeMcpSession("GnOuGo.Agent.Mcp")),
            new LLMOptions
            {
                DefaultProvider = "openai",
                DefaultModel = "gpt-4o-mini",
                Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ollama"] = new() { Url = "http://localhost:11434", Type = "ollama" }
                }
            },
            keyVaultStore);

        var events = await SmartFlowTestFactory.CollectAsync(service.ExecuteAsync("/agent add", CancellationToken.None));

        var answer = Assert.Single(events);
        Assert.Equal("answer", answer.Type);
        Assert.Equal("❌ Configure a default LLM provider first. Use `/llm add` to create one, then `/llm default` before retrying `/agent add`.", answer.Text);
        Assert.Equal(0, llm.CallCount);
    }
}

