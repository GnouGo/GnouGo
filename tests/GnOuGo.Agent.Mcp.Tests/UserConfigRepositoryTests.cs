using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

