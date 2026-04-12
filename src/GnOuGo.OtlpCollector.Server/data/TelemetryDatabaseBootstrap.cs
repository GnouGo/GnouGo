using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace OtlpTenantCollector.Data;

internal static class TelemetryDatabaseBootstrap
{
    public static async Task EnsureInitializedAsync(TelemetryDbContext db, bool resetSchema, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (resetSchema)
            await DropTablesAsync(db, ct);

        if (await TableExistsAsync(db, "tenants", ct))
            return;

        var creator = db.GetService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync(ct);
    }

    private static async Task DropTablesAsync(TelemetryDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"log_records\";", ct);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"span_records\";", ct);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"tenants\";", ct);
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
