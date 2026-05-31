using GnOuGo.Files.Server.Models;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Files.Server.Data;

public sealed class FilesDbContext : DbContext
{
    [UnconditionalSuppressMessage(
        "AOT",
        "IL2026",
        Justification = "Files SQLite access uses an EF Core compiled model. The final Native AOT executable is published and smoke-tested to validate this EF Core usage.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Files SQLite access uses an EF Core compiled model. The final Native AOT executable is published and smoke-tested to validate this EF Core usage.")]
    public FilesDbContext(DbContextOptions<FilesDbContext> options) : base(options)
    {
    }

    public DbSet<FileRecord> Files => Set<FileRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileRecord>(entity =>
        {
            entity.ToTable("files");
            entity.HasKey(file => file.Id);
            entity.Property(file => file.Id).HasColumnName("id").HasMaxLength(64).IsRequired();
            entity.Property(file => file.TenantId).HasColumnName("tenant_id").HasMaxLength(128).IsRequired();
            entity.Property(file => file.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(512).IsRequired();
            entity.Property(file => file.ContentType).HasColumnName("content_type").HasMaxLength(256).IsRequired();
            entity.Property(file => file.StoredFileName).HasColumnName("stored_file_name").HasMaxLength(128).IsRequired();
            entity.Property(file => file.StoredPath).HasColumnName("stored_path").HasMaxLength(2048).IsRequired();
            entity.Property(file => file.SizeBytes).HasColumnName("size_bytes");
            entity.Property(file => file.CreatedUtc).HasColumnName("created_utc");
            entity.Property(file => file.ExpiresUtc).HasColumnName("expires_utc");

            entity.HasIndex(file => new { file.TenantId, file.ExpiresUtc });
            entity.HasIndex(file => file.ExpiresUtc);
        });
    }
}

