using GnOuGo.Agent.Mcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class AgentMcpDatabaseBootstrapTests : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public AgentMcpDatabaseBootstrapTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "gnougo-agent-mcp-bootstrap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "agent.db");
    }

    private AgentMcpDbContext CreateDbContext()
    {
        return new AgentMcpDbContext(new DbContextOptionsBuilder<AgentMcpDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options);
    }

    [Fact]
    public async Task EnsureCreatedAsync_CreatesAllTables()
    {
        await using var db = CreateDbContext();
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(db);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "UserConfigs"));
        Assert.True(await IndexExistsAsync(connection, "IX_UserConfigs_TenantScopeKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_UserConfigs_TenantId"));
    }

    [Fact]
    public async Task EnsureCreatedAsync_IsIdempotent_WhenCalledTwice()
    {
        await using (var db1 = CreateDbContext())
            await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(db1);

        await using (var db2 = CreateDbContext())
            await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(db2);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "UserConfigs"));
        Assert.True(await IndexExistsAsync(connection, "IX_UserConfigs_TenantScopeKey"));
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

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        var result = await command.ExecuteScalarAsync();
        return result is not null && result != DBNull.Value;
    }
}
