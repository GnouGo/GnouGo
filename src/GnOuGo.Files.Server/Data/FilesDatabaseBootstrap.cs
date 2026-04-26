using GnOuGo.Files.Server.Options;
using Microsoft.Data.Sqlite;

namespace GnOuGo.Files.Server.Data;

public static class FilesDatabaseBootstrap
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var paths = scope.ServiceProvider.GetRequiredService<FilesStoragePaths>();
        Directory.CreateDirectory(paths.StorageRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS files (
                id TEXT NOT NULL PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                original_file_name TEXT NOT NULL,
                content_type TEXT NOT NULL,
                stored_file_name TEXT NOT NULL,
                stored_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                expires_utc TEXT NOT NULL
            );
            """, cancellationToken);

        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_files_tenant_id_expires_utc ON files (tenant_id, expires_utc);", cancellationToken);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_files_expires_utc ON files (expires_utc);", cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}


