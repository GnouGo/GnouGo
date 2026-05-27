using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Services;
using Microsoft.Data.Sqlite;

namespace GnOuGo.Agent.Mcp;

internal static class AgentMcpDatabaseBootstrap
{
    public static async Task EnsureCreatedAsync(AgentSqliteStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        await using var connection = store.OpenConnection();

        await ExecuteAsync(connection, AgentSqliteSchema.CreateAgentsTable, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateAgentsTenantIndex, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateAgentsNameTenantIndex, ct);

        await ExecuteAsync(connection, AgentSqliteSchema.CreateUserConfigsTable, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateUserConfigsTenantScopeIndex, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateUserConfigsTenantIndex, ct);
        await EnsureColumnAsync(connection, "UserConfigs", "DefaultEmbeddingConfig", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, "UserConfigs", "ModelOverridesJson", "TEXT NULL", ct);

        await ExecuteAsync(connection, AgentSqliteSchema.CreateDiffEntriesTable, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateDiffEntriesEntityTimestampIndex, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateDiffEntriesEntityIndex, ct);
        await ExecuteAsync(connection, AgentSqliteSchema.CreateDiffEntriesTimestampIndex, ct);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition, CancellationToken ct)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        await ExecuteAsync(connection, $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDefinition};", ct);
    }
}
