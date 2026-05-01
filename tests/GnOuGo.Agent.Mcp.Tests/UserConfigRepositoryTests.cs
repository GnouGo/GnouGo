using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class UserConfigRepositoryTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentDbContext _db;
    private readonly UserConfigRepository _repository;

    public UserConfigRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
        _repository = new UserConfigRepository(_db);
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
            ModelOverrides: new JsonObject
            {
                ["local/custom"] = new JsonObject
                {
                    ["id"] = "local/custom",
                    ["providerType"] = "ollama",
                    ["contextWindowTokens"] = 32768,
                    ["maxInputTokens"] = 32768,
                    ["maxOutputTokens"] = 4096,
                    ["pricing"] = new JsonObject { ["inputPer1MTokens"] = 0, ["outputPer1MTokens"] = 0 },
                    ["capabilities"] = new JsonObject
                    {
                        ["supportsTemperature"] = true,
                        ["supportsReasoningEffort"] = false,
                        ["supportsStructuredOutput"] = true,
                        ["supportsTools"] = true,
                        ["supportsJsonMode"] = true
                    }
                }
            }));

        var snapshot = await _repository.GetAsync();

        Assert.NotNull(snapshot.ModelOverrides);
        var overrides = snapshot.ModelOverrides!;
        var metadata = Assert.Single(overrides).Value!.AsObject();
        Assert.Equal("local/custom", metadata["id"]!.GetValue<string>());
        Assert.Equal(32768, metadata["contextWindowTokens"]!.GetValue<int>());
        Assert.Equal(0, metadata["pricing"]!.AsObject()["inputPer1MTokens"]!.GetValue<int>());
        Assert.True(metadata["capabilities"]!.AsObject()["supportsTools"]!.GetValue<bool>());
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

