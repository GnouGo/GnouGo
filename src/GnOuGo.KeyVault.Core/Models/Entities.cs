namespace GnOuGo.KeyVault.Core.Models;

public enum AuditOperation
{
    CreateTenant,
    DeleteTenant,
    SetSecret,
    GetSecret,
    DeleteSecret,
    ListSecrets,
    GetVersions,
    ListTenants,
    GetAudit
}

// ── Entities ─────────────────────────────────────────────────────────

public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string PublicKeyPem { get; set; }
    public required string PrivateKeyPem { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string CreatedBy { get; set; }
    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt
    {
        get => new(CreatedAtTicks, TimeSpan.Zero);
        set => CreatedAtTicks = value.UtcTicks;
    }
}

public sealed class Secret
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public Guid? TenantId { get; set; }
    public bool IsDeleted { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt
    {
        get => new(CreatedAtTicks, TimeSpan.Zero);
        set => CreatedAtTicks = value.UtcTicks;
    }

    public Tenant? Tenant { get; set; }
    public ICollection<SecretVersion> Versions { get; set; } = [];
}

public sealed class SecretVersion
{
    public Guid Id { get; set; }
    public Guid SecretId { get; set; }
    public int Version { get; set; }
    public required string EncryptedValue { get; set; }
    public long CreatedAtTicks { get; set; }
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt
    {
        get => new(CreatedAtTicks, TimeSpan.Zero);
        set => CreatedAtTicks = value.UtcTicks;
    }

    public Secret? Secret { get; set; }
}

public sealed class AuditEntry
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string? SecretKey { get; set; }
    public AuditOperation Operation { get; set; }
    public required string Author { get; set; }
    public long TimestampTicks { get; set; }
    public string? Details { get; set; }

    public DateTimeOffset Timestamp
    {
        get => new(TimestampTicks, TimeSpan.Zero);
        set => TimestampTicks = value.UtcTicks;
    }
}

