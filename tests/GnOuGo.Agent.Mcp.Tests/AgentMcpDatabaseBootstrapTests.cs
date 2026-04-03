using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Diff.Core.Data;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class AgentMcpDatabaseBootstrapTests : IAsyncDisposable
{
    private readonly SqliteConnection _agentConnection;
    private readonly SqliteConnection _keyVaultConnection;
    private readonly AgentDbContext _agentDb;
    private readonly DiffDbContext _diffDb;
    private readonly KeyVaultDbContext _keyVaultDb;
    private readonly KeyVaultService _keyVaultService;  

    public AgentMcpDatabaseBootstrapTests()
    {
        _agentConnection = new SqliteConnection("Data Source=:memory:");
        _agentConnection.Open();

        _keyVaultConnection = new SqliteConnection("Data Source=:memory:");
        _keyVaultConnection.Open();

        _agentDb = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_agentConnection)
            .Options);
        _diffDb = new DiffDbContext(new DbContextOptionsBuilder<DiffDbContext>()
            .UseSqlite(_agentConnection)
            .Options);
        _keyVaultDb = new KeyVaultDbContext(new DbContextOptionsBuilder<KeyVaultDbContext>()
            .UseSqlite(_keyVaultConnection)
            .Options);
        _keyVaultService = new KeyVaultService(_keyVaultDb);
    }

    [Fact]
    public async Task EnsureCreatedAsync_CreatesDiffEntries_WhenAgentSchemaAlreadyExists()
    {
        await _agentDb.Database.EnsureCreatedAsync();

        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(
            _agentDb,
            _diffDb,
            _keyVaultDb,
            _keyVaultService);

        Assert.True(await TableExistsAsync(_agentConnection, "Agents"));
        Assert.True(await TableExistsAsync(_agentConnection, "DiffEntries"));
    }

    [Fact]
    public async Task EnsureCreatedAsync_IsIdempotent_WhenCalledTwice()
    {
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(
            _agentDb,
            _diffDb,
            _keyVaultDb,
            _keyVaultService);
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(
            _agentDb,
            _diffDb,
            _keyVaultDb,
            _keyVaultService);

        Assert.True(await TableExistsAsync(_agentConnection, "Agents"));
        Assert.True(await TableExistsAsync(_agentConnection, "DiffEntries"));
    }

    public async ValueTask DisposeAsync()
    {
        await _agentDb.DisposeAsync();
        await _diffDb.DisposeAsync();
        await _keyVaultDb.DisposeAsync();
        await _agentConnection.DisposeAsync();
        await _keyVaultConnection.DisposeAsync();
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

