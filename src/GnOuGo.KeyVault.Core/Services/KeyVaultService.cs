using Microsoft.EntityFrameworkCore;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Models;

namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Multi-tenant encrypted secret manager with versioning and audit trail.
/// </summary>
public sealed class KeyVaultService
{
    private readonly KeyVaultDbContext _db;

    // Default tenant RSA keypair (lazy-initialized)
    private static string? _defaultPublicPem;
    private static string? _defaultPrivatePem;
    private static readonly object _lock = new();

    public KeyVaultService(KeyVaultDbContext db) => _db = db;

    // ── Tenant management ────────────────────────────────────────────

    public async Task<TenantDto> CreateTenantAsync(string name, string author, CancellationToken ct = default)
    {
        var (pub, priv) = CryptoService.GenerateKeyPair();
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            PublicKeyPem = pub,
            PrivateKeyPem = priv,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            CreatedBy = author
        };

        _db.Tenants.Add(tenant);
        LogAudit(null, null, AuditOperation.CreateTenant, author, $"Created tenant '{name}'");
        await _db.SaveChangesAsync(ct);

        return ToDto(tenant);
    }

    public async Task<List<TenantDto>> ListTenantsAsync(CancellationToken ct = default)
    {
        var tenants = new List<TenantDto>();
        await foreach (var t in KeyVaultQueries.GetActiveNonDefaultTenants(_db))
            tenants.Add(new TenantDto(t.Id, t.Name, new DateTimeOffset(t.CreatedAtTicks, TimeSpan.Zero), t.CreatedBy));
        return tenants;
    }

    public async Task<bool> DeleteTenantAsync(Guid tenantId, string author, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null || tenant.IsDeleted) return false;

        tenant.IsDeleted = true;
        LogAudit(tenantId, null, AuditOperation.DeleteTenant, author, $"Deleted tenant '{tenant.Name}'");
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Secret management ────────────────────────────────────────────

    public async Task<SecretValueDto> SetSecretAsync(string key, string value, Guid? tenantId, string author, CancellationToken ct = default)
    {
        var (pubPem, _) = await GetKeyPairAsync(tenantId, ct);
        var encrypted = CryptoService.Encrypt(value, pubPem);

        var secret = await KeyVaultQueries.GetSecretByKeyAndTenant(_db, key, tenantId);

        if (secret is null)
        {
            secret = new Secret
            {
                Id = Guid.CreateVersion7(),
                Key = key,
                TenantId = tenantId,
                CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
                CreatedBy = author
            };
            _db.Secrets.Add(secret);
        }

        var nextVersion = secret.Versions.Count > 0 ? secret.Versions.Max(v => v.Version) + 1 : 1;
        var version = new SecretVersion
        {
            Id = Guid.CreateVersion7(),
            SecretId = secret.Id,
            Version = nextVersion,
            EncryptedValue = encrypted,
            CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
            CreatedBy = author
        };

        _db.SecretVersions.Add(version);
        LogAudit(tenantId, key, AuditOperation.SetSecret, author, $"Set version {nextVersion}");
        await _db.SaveChangesAsync(ct);

        return new SecretValueDto(secret.Id, key, value, nextVersion, tenantId, version.CreatedAt);
    }

    public async Task<SecretValueDto?> GetSecretAsync(string key, Guid? tenantId, string author, CancellationToken ct = default)
    {
        var secret = await KeyVaultQueries.GetSecretByKeyAndTenant(_db, key, tenantId);

        if (secret is null) return null;

        var latestVersion = secret.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
        if (latestVersion is null) return null;

        var (_, privPem) = await GetKeyPairAsync(tenantId, ct);
        var decrypted = CryptoService.Decrypt(latestVersion.EncryptedValue, privPem);

        LogAudit(tenantId, key, AuditOperation.GetSecret, author, $"Read version {latestVersion.Version}");
        await _db.SaveChangesAsync(ct);

        return new SecretValueDto(secret.Id, key, decrypted, latestVersion.Version, tenantId, latestVersion.CreatedAt);
    }

    public async Task<bool> DeleteSecretAsync(string key, Guid? tenantId, string author, CancellationToken ct = default)
    {
        var secret = await KeyVaultQueries.GetSecretByKeyAndTenantNoVersions(_db, key, tenantId);

        if (secret is null) return false;

        secret.IsDeleted = true;
        LogAudit(tenantId, key, AuditOperation.DeleteSecret, author);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<SecretDto>> ListSecretsAsync(Guid? tenantId, CancellationToken ct = default)
    {
        var query = _db.Secrets
            .AsNoTracking()
            .Include(s => s.Versions)
            .Include(s => s.Tenant)
            .Where(s => !s.IsDeleted);

        // null tenantId = filter for default tenant (TenantId IS NULL)
        query = tenantId.HasValue
            ? query.Where(s => s.TenantId == tenantId.Value)
            : query.Where(s => s.TenantId == null);

        return await query
            .OrderBy(s => s.Key)
            .Select(s => new SecretDto(
                s.Id,
                s.Key,
                s.TenantId,
                s.Tenant != null ? s.Tenant.Name : null,
                s.Versions.Any() ? s.Versions.Max(v => v.Version) : 0,
                new DateTimeOffset(s.CreatedAtTicks, TimeSpan.Zero),
                s.CreatedBy))
            .ToListAsync(ct);
    }

    public async Task<List<SecretVersionDto>> GetSecretVersionsAsync(string key, Guid? tenantId, CancellationToken ct = default)
    {
        var secret = await KeyVaultQueries.GetSecretByKeyAndTenant(_db, key, tenantId);

        if (secret is null) return [];

        return secret.Versions
            .OrderByDescending(v => v.Version)
            .Select(v => new SecretVersionDto(v.Id, v.Version, v.CreatedAt, v.CreatedBy))
            .ToList();
    }

    // ── Audit log ────────────────────────────────────────────────────

    public async Task<List<AuditEntryDto>> GetAuditLogAsync(Guid? tenantId, string? secretKey, int skip, int take, CancellationToken ct = default)
    {
        var query = _db.AuditEntries.AsNoTracking().AsQueryable();

        if (tenantId.HasValue)
            query = query.Where(a => a.TenantId == tenantId.Value);
        if (!string.IsNullOrWhiteSpace(secretKey))
            query = query.Where(a => a.SecretKey == secretKey);

        return await query
            .OrderByDescending(a => a.TimestampTicks)
            .Skip(skip)
            .Take(take)
            .Select(a => new AuditEntryDto(
                a.Id, a.TenantId, a.SecretKey,
                a.Operation.ToString(), a.Author,
                new DateTimeOffset(a.TimestampTicks, TimeSpan.Zero),
                a.Details))
            .ToListAsync(ct);
    }

    // ── Default tenant bootstrap ─────────────────────────────────────

    /// <summary>
    /// Ensures a default RSA key pair exists for secrets with no tenant (TenantId = null).
    /// Called once at application startup.
    /// </summary>
    public async Task EnsureDefaultKeyPairAsync(CancellationToken ct = default)
    {
        const string defaultName = "__default__";
        var defaultTenant = await KeyVaultQueries.GetTenantByName(_db, defaultName);

        if (defaultTenant is null)
        {
            var (pub, priv) = CryptoService.GenerateKeyPair();
            defaultTenant = new Tenant
            {
                Id = Guid.CreateVersion7(),
                Name = defaultName,
                PublicKeyPem = pub,
                PrivateKeyPem = priv,
                CreatedAtTicks = DateTimeOffset.UtcNow.UtcTicks,
                CreatedBy = "system"
            };
            _db.Tenants.Add(defaultTenant);
            await _db.SaveChangesAsync(ct);
        }

        lock (_lock)
        {
            _defaultPublicPem = defaultTenant.PublicKeyPem;
            _defaultPrivatePem = defaultTenant.PrivateKeyPem;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private async Task<(string PublicPem, string PrivatePem)> GetKeyPairAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            lock (_lock)
            {
                if (_defaultPublicPem is not null && _defaultPrivatePem is not null)
                    return (_defaultPublicPem, _defaultPrivatePem);
            }
            await EnsureDefaultKeyPairAsync(ct);
            lock (_lock)
            {
                return (_defaultPublicPem!, _defaultPrivatePem!);
            }
        }

        var tenant = await _db.Tenants.FindAsync([tenantId.Value], ct)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' not found.");

        if (tenant.IsDeleted)
            throw new InvalidOperationException($"Tenant '{tenantId}' has been deleted.");

        return (tenant.PublicKeyPem, tenant.PrivateKeyPem);
    }

    private void LogAudit(Guid? tenantId, string? secretKey, AuditOperation op, string author, string? details = null)
    {
        _db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SecretKey = secretKey,
            Operation = op,
            Author = author,
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Details = details
        });
    }

    private static TenantDto ToDto(Tenant t) => new(t.Id, t.Name, t.CreatedAt, t.CreatedBy);
}

