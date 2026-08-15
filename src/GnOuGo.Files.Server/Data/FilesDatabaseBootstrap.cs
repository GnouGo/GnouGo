using GnOuGo.Files.Server.Options;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace GnOuGo.Files.Server.Data;

public static class FilesDatabaseBootstrap
{
    // Generated from FilesDbContext for the Native AOT path. Keep this schema in
    // lockstep with the compiled model; repository reads and writes remain EF Core.
    private const string NativeAotSchema = """
        CREATE TABLE IF NOT EXISTS "files" (
            "id" TEXT NOT NULL CONSTRAINT "PK_files" PRIMARY KEY,
            "content_type" TEXT NOT NULL,
            "created_utc" TEXT NOT NULL,
            "expires_utc" TEXT NOT NULL,
            "original_file_name" TEXT NOT NULL,
            "size_bytes" INTEGER NOT NULL,
            "stored_file_name" TEXT NOT NULL,
            "stored_path" TEXT NOT NULL,
            "tenant_id" TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_files_expires_utc" ON "files" ("expires_utc");
        CREATE INDEX IF NOT EXISTS "IX_files_tenant_id_expires_utc" ON "files" ("tenant_id", "expires_utc");
        """;

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var paths = scope.ServiceProvider.GetRequiredService<FilesStoragePaths>();
        Directory.CreateDirectory(paths.StorageRootPath);
        var databaseDirectory = Path.GetDirectoryName(paths.DatabasePath);
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            throw new InvalidOperationException($"The Files Server database path has no directory: '{paths.DatabasePath}'.");

        Directory.CreateDirectory(databaseDirectory);

        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        await EnsureDatabaseCreatedAsync(db, cancellationToken);
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "EnsureCreatedAsync is executed only when dynamic code is available. Native AOT uses the pre-generated schema below, then all persistence continues through EF Core and its compiled model.")]
    private static Task EnsureDatabaseCreatedAsync(FilesDbContext db, CancellationToken cancellationToken)
        => RuntimeFeature.IsDynamicCodeSupported
            ? db.Database.EnsureCreatedAsync(cancellationToken)
            : EnsureNativeAotSchemaCreatedAsync(db, cancellationToken);

    private static async Task EnsureNativeAotSchemaCreatedAsync(FilesDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = NativeAotSchema;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }
}
