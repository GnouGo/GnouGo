using Microsoft.Data.Sqlite;

namespace OtlpTenantCollector.Data;

internal static class TelemetryDatabaseBootstrap
{
    public static async Task EnsureInitializedAsync(string databasePath, bool resetSchema, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(ct);

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", ct);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", ct);

        if (resetSchema)
            await DropTablesAsync(connection, ct);

        if (await TableExistsAsync(connection, "tenants", ct))
            return;

        await ExecuteAsync(connection, SchemaSql, ct);
    }

    private static async Task DropTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS log_records;", ct);
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS span_records;", ct);
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS tenants;", ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);

        var result = await command.ExecuteScalarAsync(ct);
        return result is not null && result != DBNull.Value;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string SchemaSql = """
        CREATE TABLE tenants (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            retention_minutes INTEGER NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE TABLE span_records (
            id TEXT NOT NULL PRIMARY KEY,
            tenant_id TEXT NULL,
            received_utc TEXT NOT NULL,
            trace_id BLOB NOT NULL,
            span_id BLOB NOT NULL,
            parent_span_id BLOB NULL,
            name TEXT NOT NULL,
            kind INTEGER NOT NULL,
            start_unix_ns INTEGER NOT NULL,
            end_unix_ns INTEGER NOT NULL,
            status_code INTEGER NOT NULL,
            status_message TEXT NULL,
            attributes_json TEXT NULL,
            events_json TEXT NULL,
            resource_json TEXT NULL,
            scope_json TEXT NULL,
            service_name TEXT NULL
        );

        CREATE INDEX IX_span_records_tenant_id_trace_id ON span_records (tenant_id, trace_id);
        CREATE INDEX IX_span_records_tenant_id_received_utc ON span_records (tenant_id, received_utc);

        CREATE TABLE log_records (
            id TEXT NOT NULL PRIMARY KEY,
            tenant_id TEXT NULL,
            received_utc TEXT NOT NULL,
            trace_id BLOB NULL,
            span_id BLOB NULL,
            severity_number INTEGER NOT NULL,
            severity_text TEXT NULL,
            body TEXT NULL,
            attributes_json TEXT NULL,
            resource_json TEXT NULL,
            scope_json TEXT NULL,
            service_name TEXT NULL
        );

        CREATE INDEX IX_log_records_tenant_id_received_utc ON log_records (tenant_id, received_utc);
        CREATE INDEX IX_log_records_tenant_id_trace_id ON log_records (tenant_id, trace_id);
        """;
}
