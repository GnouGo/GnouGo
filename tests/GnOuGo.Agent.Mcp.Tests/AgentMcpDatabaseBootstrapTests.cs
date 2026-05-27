using Microsoft.Data.Sqlite;
using GnOuGo.Agent.Mcp.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class AgentMcpDatabaseBootstrapTests : IAsyncDisposable
{
    private readonly string _directory;
    private readonly AgentSqliteStore _store;

    public AgentMcpDatabaseBootstrapTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "gnougo-agent-mcp-bootstrap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new AgentSqliteStore(Path.Combine(_directory, "agent.db"));
    }

    [Fact]
    public async Task EnsureCreatedAsync_CreatesDiffEntries_WhenAgentSchemaAlreadyExists()
    {
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(_store);

        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(_store);

        await using var connection = _store.OpenConnection();
        Assert.True(await TableExistsAsync(connection, "Agents"));
        Assert.True(await TableExistsAsync(connection, "UserConfigs"));
        Assert.True(await TableExistsAsync(connection, "DiffEntries"));
    }

    [Fact]
    public async Task EnsureCreatedAsync_IsIdempotent_WhenCalledTwice()
    {
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(_store);
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(_store);

        await using var connection = _store.OpenConnection();
        Assert.True(await TableExistsAsync(connection, "Agents"));
        Assert.True(await TableExistsAsync(connection, "UserConfigs"));
        Assert.True(await TableExistsAsync(connection, "DiffEntries"));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
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

