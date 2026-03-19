using Microsoft.EntityFrameworkCore;
using GnOuGo.Diff.Core.Models;

namespace GnOuGo.Diff.Core.Data;

public class DiffDbContext : DbContext
{
    public DiffDbContext(DbContextOptions<DiffDbContext> options) : base(options)
    {
    }

    public DbSet<DiffEntry> DiffEntries => Set<DiffEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DiffEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.EntityType, e.EntityId, e.TimestampTicks });
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => e.TimestampTicks);
            
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Author).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ValueHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CurrentValue).IsRequired();
            entity.Property(e => e.DiffFromPrevious);
            entity.Property(e => e.TimestampTicks).IsRequired();
            
            // Ignorer la propriété calculée Timestamp
            entity.Ignore(e => e.Timestamp);
        });
    }
}

