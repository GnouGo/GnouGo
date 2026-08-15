using Microsoft.EntityFrameworkCore;
using System.Data;

namespace GnOuGo.KeyVault.Core.Data;

public static class KeyVaultDatabaseBootstrap
{
    // Generated from KeyVaultDbContext. EF Core's relational creator requests the
    // design-time model, which is intentionally unavailable in trimmed hosts.
    // Runtime reads and writes remain exclusively on KeyVaultDbContext.
    private const string TrimmedHostSchema = """
        CREATE TABLE IF NOT EXISTS Tenants (
            Id TEXT NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY,
            Name TEXT NOT NULL,
            PublicKeyPem TEXT NOT NULL,
            PrivateKeyPem TEXT NOT NULL,
            CreatedAtTicks INTEGER NOT NULL,
            CreatedBy TEXT NOT NULL,
            IsDeleted INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Tenants_Name
            ON Tenants (Name) WHERE IsDeleted = 0;

        CREATE TABLE IF NOT EXISTS Secrets (
            Id TEXT NOT NULL CONSTRAINT PK_Secrets PRIMARY KEY,
            Key TEXT NOT NULL,
            TenantId TEXT NULL,
            IsDeleted INTEGER NOT NULL,
            CreatedAtTicks INTEGER NOT NULL,
            CreatedBy TEXT NOT NULL,
            CONSTRAINT FK_Secrets_Tenants_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants (Id) ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Secrets_Key_TenantId
            ON Secrets (Key, TenantId) WHERE IsDeleted = 0;

        CREATE TABLE IF NOT EXISTS SecretVersions (
            Id TEXT NOT NULL CONSTRAINT PK_SecretVersions PRIMARY KEY,
            SecretId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            EncryptedValue TEXT NOT NULL,
            CreatedAtTicks INTEGER NOT NULL,
            CreatedBy TEXT NOT NULL,
            CONSTRAINT FK_SecretVersions_Secrets_SecretId FOREIGN KEY (SecretId) REFERENCES Secrets (Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_SecretVersions_SecretId_Version
            ON SecretVersions (SecretId, Version);

        CREATE TABLE IF NOT EXISTS AuditEntries (
            Id TEXT NOT NULL CONSTRAINT PK_AuditEntries PRIMARY KEY,
            TenantId TEXT NULL,
            SecretKey TEXT NULL,
            Operation TEXT NOT NULL,
            Author TEXT NOT NULL,
            TimestampTicks INTEGER NOT NULL,
            Details TEXT NULL,
            CONSTRAINT FK_AuditEntries_Tenants_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants (Id)
        );
        CREATE INDEX IF NOT EXISTS IX_AuditEntries_TenantId_TimestampTicks
            ON AuditEntries (TenantId, TimestampTicks);
        CREATE INDEX IF NOT EXISTS IX_AuditEntries_TimestampTicks
            ON AuditEntries (TimestampTicks);
        """;

    public static async Task EnsureCreatedAsync(KeyVaultDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await TableExistsAsync(db, "Tenants", ct))
            return;

        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await connection.OpenAsync(ct);

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = TrimmedHostSchema;
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(DbContext dbContext, string tableName, CancellationToken ct)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection?.State != ConnectionState.Open)
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
