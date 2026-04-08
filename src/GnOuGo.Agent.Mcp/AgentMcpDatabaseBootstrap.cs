using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Diff.Core.Data;

namespace GnOuGo.Agent.Mcp;

internal static class AgentMcpDatabaseBootstrap
{
    public static async Task EnsureCreatedAsync(
        AgentDbContext agentDb,
        DiffDbContext diffDb,
        CancellationToken ct = default)
    {
        if (!await TableExistsAsync(agentDb, "Agents", ct))
        {
            var agentCreator = agentDb.GetService<IRelationalDatabaseCreator>();
            await agentCreator.CreateTablesAsync(ct);
        }

        if (!await TableExistsAsync(diffDb, "DiffEntries", ct))
        {
            var creator = diffDb.GetService<IRelationalDatabaseCreator>();
            await creator.CreateTablesAsync(ct);
        }
    }

    private static async Task<bool> TableExistsAsync(DbContext dbContext, string tableName, CancellationToken ct)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection?.State != System.Data.ConnectionState.Open)
            await command.Connection!.OpenAsync(ct);

        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(ct);
        return result is not null && result != DBNull.Value;
    }
}


