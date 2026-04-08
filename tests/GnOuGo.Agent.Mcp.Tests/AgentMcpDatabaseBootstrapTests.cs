using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Diff.Core.Data;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class AgentMcpDatabaseBootstrapTests : IAsyncDisposable
{
    private readonly SqliteConnection _agentConnection;
    private readonly AgentDbContext _agentDb;
    private readonly DiffDbContext _diffDb;

    public AgentMcpDatabaseBootstrapTests()
    {
        _agentConnection = new SqliteConnection("Data Source=:memory:");
        _agentConnection.Open();

        _agentDb = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_agentConnection)
            .Options);
        _diffDb = new DiffDbContext(new DbContextOptionsBuilder<DiffDbContext>()
            .UseSqlite(_agentConnection)
            .Options);
    }

    [Fact]
    public async Task EnsureCreatedAsync_CreatesDiffEntries_WhenAgentSchemaAlreadyExists()
    {
        await _agentDb.Database.EnsureCreatedAsync();

        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(
            _agentDb,
            _diffDb);

        Assert.True(await TableExistsAsync(_agentConnection, "Agents"));
        Assert.True(await TableExistsAsync(_agentConnection, "DiffEntries"));
    }

    [Fact]
    public async Task EnsureCreatedAsync_IsIdempotent_WhenCalledTwice()
    {
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(
            _agentDb,
            _diffDb);
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(
            _agentDb,
            _diffDb);

        Assert.True(await TableExistsAsync(_agentConnection, "Agents"));
        Assert.True(await TableExistsAsync(_agentConnection, "DiffEntries"));
    }

    public async ValueTask DisposeAsync()
    {
        await _agentDb.DisposeAsync();
        await _diffDb.DisposeAsync();
        await _agentConnection.DisposeAsync();
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }
}

