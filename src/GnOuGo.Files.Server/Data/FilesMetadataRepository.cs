using System.Globalization;
using GnOuGo.Files.Server.Models;
using GnOuGo.Files.Server.Options;
using Microsoft.Data.Sqlite;

namespace GnOuGo.Files.Server.Data;

public sealed class FilesMetadataRepository
{
    private readonly FilesStoragePaths _paths;

    public FilesMetadataRepository(FilesStoragePaths paths)
    {
        _paths = paths;
    }

    public async Task InsertAsync(FileRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files (
                id,
                tenant_id,
                original_file_name,
                content_type,
                stored_file_name,
                stored_path,
                size_bytes,
                created_utc,
                expires_utc)
            VALUES (
                $id,
                $tenant_id,
                $original_file_name,
                $content_type,
                $stored_file_name,
                $stored_path,
                $size_bytes,
                $created_utc,
                $expires_utc);
            """;
        AddParameters(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FileRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   tenant_id,
                   original_file_name,
                   content_type,
                   stored_file_name,
                   stored_path,
                   size_bytes,
                   created_utc,
                   expires_utc
            FROM files
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadRecord(reader);
    }

    public async Task<List<FileRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   tenant_id,
                   original_file_name,
                   content_type,
                   stored_file_name,
                   stored_path,
                   size_bytes,
                   created_utc,
                   expires_utc
            FROM files;
            """;

        var records = new List<FileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(ReadRecord(reader));

        return records;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DatabasePath)!);
        var connection = new SqliteConnection($"Data Source={_paths.DatabasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddParameters(SqliteCommand command, FileRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$tenant_id", record.TenantId);
        command.Parameters.AddWithValue("$original_file_name", record.OriginalFileName);
        command.Parameters.AddWithValue("$content_type", record.ContentType);
        command.Parameters.AddWithValue("$stored_file_name", record.StoredFileName);
        command.Parameters.AddWithValue("$stored_path", record.StoredPath);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$created_utc", FormatUtc(record.CreatedUtc));
        command.Parameters.AddWithValue("$expires_utc", FormatUtc(record.ExpiresUtc));
    }

    private static FileRecord ReadRecord(SqliteDataReader reader)
    {
        return new FileRecord
        {
            Id = reader.GetString(0),
            TenantId = reader.GetString(1),
            OriginalFileName = reader.GetString(2),
            ContentType = reader.GetString(3),
            StoredFileName = reader.GetString(4),
            StoredPath = reader.GetString(5),
            SizeBytes = reader.GetInt64(6),
            CreatedUtc = ParseUtc(reader.GetString(7)),
            ExpiresUtc = ParseUtc(reader.GetString(8))
        };
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}

