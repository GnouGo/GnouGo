using GnOuGo.KeyVault.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.KeyVault.Core.Data;

/// <summary>
/// Precompiled EF Core queries for optimal performance and AOT friendliness.
/// </summary>
internal static class KeyVaultQueries
{
    // ── Tenant queries ───────────────────────────────────────────────

    public static readonly Func<KeyVaultDbContext, string, Task<Tenant?>> GetTenantByName =
        EF.CompileAsyncQuery(
            (KeyVaultDbContext db, string name) =>
                db.Tenants.FirstOrDefault(t => t.Name == name));

    public static readonly Func<KeyVaultDbContext, IAsyncEnumerable<Tenant>> GetActiveNonDefaultTenants =
        EF.CompileAsyncQuery(
            (KeyVaultDbContext db) =>
                db.Tenants
                    .Where(t => !t.IsDeleted && t.Name != "__default__")
                    .OrderBy(t => t.Name)
                    .AsQueryable());

    // ── Secret queries ───────────────────────────────────────────────

    public static readonly Func<KeyVaultDbContext, string, Guid?, Task<Secret?>> GetSecretByKeyAndTenant =
        EF.CompileAsyncQuery(
            (KeyVaultDbContext db, string key, Guid? tenantId) =>
                db.Secrets
                    .Include(s => s.Versions)
                    .FirstOrDefault(s => s.Key == key && s.TenantId == tenantId && !s.IsDeleted));

    public static readonly Func<KeyVaultDbContext, string, Guid?, Task<Secret?>> GetSecretByKeyAndTenantNoVersions =
        EF.CompileAsyncQuery(
            (KeyVaultDbContext db, string key, Guid? tenantId) =>
                db.Secrets
                    .FirstOrDefault(s => s.Key == key && s.TenantId == tenantId && !s.IsDeleted));

    // ── Audit queries ────────────────────────────────────────────────

    public static readonly Func<KeyVaultDbContext, Guid, IAsyncEnumerable<AuditEntry>> GetAuditByTenant =
        EF.CompileAsyncQuery(
            (KeyVaultDbContext db, Guid tenantId) =>
                db.AuditEntries
                    .Where(a => a.TenantId == tenantId)
                    .OrderByDescending(a => a.TimestampTicks)
                    .AsQueryable());
}



