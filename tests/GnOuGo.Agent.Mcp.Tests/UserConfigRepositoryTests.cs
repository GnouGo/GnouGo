using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class UserConfigRepositoryTests : IDisposable
{
    private readonly AgentMcpTestDatabase _database;
    private readonly UserConfigRepository _repository;

    public UserConfigRepositoryTests()
    {
        _database = new AgentMcpTestDatabase();
        _repository = _database.CreateUserConfigRepository();
    }

    [Fact]
    public async Task GetAsync_ReturnsEmptySnapshot_WhenNoRowExists()
    {
        var snapshot = await _repository.GetAsync();

        Assert.Null(snapshot.DefaultLlmProvider);
        Assert.Null(snapshot.DefaultLlmModel);
        Assert.Null(snapshot.DefaultAgent);
        Assert.Null(snapshot.UpdatedAt);
    }

    [Fact]
    public async Task SetAsync_PersistsAndReadsBack_Defaults()
    {
        await _repository.SetAsync(new UserConfigUpdate(
            DefaultLlmProvider: "ollama",
            DefaultLlmModel: "llama3",
            DefaultAgent: "slimfaas"));

        var snapshot = await _repository.GetAsync();

        Assert.Equal("ollama", snapshot.DefaultLlmProvider);
        Assert.Equal("llama3", snapshot.DefaultLlmModel);
        Assert.Equal("slimfaas", snapshot.DefaultAgent);
        Assert.NotNull(snapshot.UpdatedAt);
    }

    [Fact]
    public async Task SetAsync_Clears_Defaults_WhenRequested()
    {
        await _repository.SetAsync(new UserConfigUpdate("openai", "gpt-4o-mini", "agent-a"));

        var snapshot = await _repository.SetAsync(new UserConfigUpdate(
            DefaultLlmProvider: null,
            DefaultLlmModel: null,
            DefaultAgent: null,
            ClearDefaultLlm: true,
            ClearDefaultAgent: true));

        Assert.Null(snapshot.DefaultLlmProvider);
        Assert.Null(snapshot.DefaultLlmModel);
        Assert.Null(snapshot.DefaultAgent);
    }

    [Fact]
    public async Task SetAsync_PersistsAndReadsBack_ModelOverrides()
    {
        await _repository.SetAsync(new UserConfigUpdate(
            DefaultLlmProvider: null,
            DefaultLlmModel: null,
            DefaultAgent: null,
            ModelOverrides: new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["local/custom"] = new LLMModelMetadata
                {
                    Id = "local/custom",
                    ProviderType = "ollama",
                    ContextWindowTokens = 32768,
                    MaxInputTokens = 32768,
                    MaxOutputTokens = 4096,
                    Pricing = new ModelPricingMetadata { InputPer1MTokens = 0, OutputPer1MTokens = 0 },
                    Capabilities = new ModelCapabilityMetadata
                    {
                        SupportsTemperature = true,
                        SupportsReasoningEffort = false,
                        SupportsStructuredOutput = true,
                        SupportsTools = true,
                        SupportsJsonMode = true
                    }
                }
            }));

        var snapshot = await _repository.GetAsync();

        Assert.NotNull(snapshot.ModelOverrides);
        var overrides = snapshot.ModelOverrides!;
        var metadata = Assert.Single(overrides).Value;
        Assert.Equal("local/custom", metadata.Id);
        Assert.Equal(32768, metadata.ContextWindowTokens);
        Assert.Equal(0, metadata.Pricing!.InputPer1MTokens);
        Assert.True(metadata.Capabilities.SupportsTools);
    }

    [Fact]
    public async Task SetAsync_CreatesAuditRevision_ForEverySave()
    {
        var update = new UserConfigUpdate("ollama", "llama3", "slimfaas");

        await _repository.SetAsync(update);
        await _repository.SetAsync(update);

        var revisions = await _database.DiffService.GetRevisionsAsync("AgentConfiguration", "global");
        Assert.Equal(2, revisions.Count);
        Assert.All(revisions, revision => Assert.Contains("llama3", revision.CurrentValue));
    }

    public void Dispose() => _database.Dispose();
}

