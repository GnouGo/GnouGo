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

        await EnsureUserConfigsTableAsync(agentDb, ct);

        if (!await TableExistsAsync(diffDb, "DiffEntries", ct))
        {
            var creator = diffDb.GetService<IRelationalDatabaseCreator>();
            await creator.CreateTablesAsync(ct);
        }
    }

    private static async Task EnsureUserConfigsTableAsync(AgentDbContext agentDb, CancellationToken ct)
    {
        if (await TableExistsAsync(agentDb, "UserConfigs", ct))
            return;

        await agentDb.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UserConfigs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_UserConfigs" PRIMARY KEY,
                "TenantId" TEXT NULL,
                "TenantScopeKey" TEXT NOT NULL,
                "DefaultLlmProvider" TEXT NULL,
                "DefaultLlmModel" TEXT NULL,
                "DefaultAgent" TEXT NULL,
                "UpdatedAtTicks" INTEGER NOT NULL
            );
            """,
            ct);

        await agentDb.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserConfigs_TenantScopeKey\" ON \"UserConfigs\" (\"TenantScopeKey\");",
            ct);

        await agentDb.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_UserConfigs_TenantId\" ON \"UserConfigs\" (\"TenantId\");",
            ct);
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


