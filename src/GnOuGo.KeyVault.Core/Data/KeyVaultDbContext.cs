using Microsoft.EntityFrameworkCore;
using GnOuGo.KeyVault.Core.Models;

namespace GnOuGo.KeyVault.Core.Data;

public sealed class KeyVaultDbContext : DbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<SecretVersion> SecretVersions => Set<SecretVersion>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public KeyVaultDbContext(DbContextOptions<KeyVaultDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder m)
    {
        // ── Tenant ───────────────────────────────────────────────────
        m.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.CreatedBy).HasMaxLength(256);
            e.Ignore(t => t.CreatedAt);
            e.HasIndex(t => t.Name).IsUnique().HasFilter("IsDeleted = 0");
        });

        // ── Secret ───────────────────────────────────────────────────
        m.Entity<Secret>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Key).HasMaxLength(512);
            e.Property(s => s.CreatedBy).HasMaxLength(256);
            e.Ignore(s => s.CreatedAt);
            e.HasIndex(s => new { s.Key, s.TenantId }).IsUnique().HasFilter("IsDeleted = 0");
            e.HasOne(s => s.Tenant).WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.SetNull);
        });

        // ── SecretVersion ────────────────────────────────────────────
        m.Entity<SecretVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.CreatedBy).HasMaxLength(256);
            e.Ignore(v => v.CreatedAt);
            e.HasIndex(v => new { v.SecretId, v.Version }).IsUnique();
            e.HasOne(v => v.Secret).WithMany(s => s.Versions).HasForeignKey(v => v.SecretId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── AuditEntry ───────────────────────────────────────────────
        m.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Author).HasMaxLength(256);
            e.Property(a => a.SecretKey).HasMaxLength(512);
            e.Property(a => a.Details).HasMaxLength(2048);
            e.Property(a => a.Operation).HasConversion<string>().HasMaxLength(64);
            e.Ignore(a => a.Timestamp);
            e.HasIndex(a => new { a.TenantId, a.TimestampTicks });
            e.HasIndex(a => a.TimestampTicks);
        });
    }
}

