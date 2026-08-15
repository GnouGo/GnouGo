using GnOuGo.Files.Server.Models;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GnOuGo.Files.Server.Data;

public sealed class FilesDbContext : DbContext
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "EF Core persistence is mandatory and this context is configured with FilesDbContextModel; the published Native AOT smoke test executes its SQLite persistence path.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "EF Core persistence is mandatory and this context is configured with FilesDbContextModel; the published Native AOT smoke test executes its SQLite persistence path.")]
    public FilesDbContext(DbContextOptions<FilesDbContext> options) : base(options)
    {
    }

    public DbSet<FileRecord> Files => Set<FileRecord>();

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Files Server uses the generated EF Core compiled model; the fallback model definition is retained for design-time tooling and verified by trimmed and Native AOT persistence tests.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Files Server uses the generated EF Core compiled model and exercises schema creation and CRUD from the published Native AOT binary.")]
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var dateTimeOffsetComparer = new ValueComparer<DateTimeOffset>(
            (left, right) => left.Equals(right),
            value => value.GetHashCode(),
            value => value);

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
            var createdUtc = entity.Property(file => file.CreatedUtc).HasColumnName("created_utc");
            createdUtc.Metadata.SetValueComparer(dateTimeOffsetComparer);
            createdUtc.Metadata.SetProviderValueComparer(dateTimeOffsetComparer);
            var expiresUtc = entity.Property(file => file.ExpiresUtc).HasColumnName("expires_utc");
            expiresUtc.Metadata.SetValueComparer(dateTimeOffsetComparer);
            expiresUtc.Metadata.SetProviderValueComparer(dateTimeOffsetComparer);

            entity.HasIndex(file => new { file.TenantId, file.ExpiresUtc });
            entity.HasIndex(file => file.ExpiresUtc);
        });
    }
}

