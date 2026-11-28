using GnOuGo.Flow.Core.Runtime;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Flow.Persistence;

/// <summary>Content-free projection shared by the planning and execution indexes.</summary>
[PrimaryKey(nameof(TenantId), nameof(OwnerKind), nameof(OwnerId))]
[Index(nameof(TenantId), nameof(Phase), nameof(DeadlineUtcTicks))]
public class OwnerRetentionIndex
{
    public string TenantId { get; set; } = "";
    public string OwnerKind { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public long Revision { get; set; }
    public string Phase { get; set; } = "";
    public long CheckpointUtcTicks { get; set; }
    public long? DeadlineUtcTicks { get; set; }
    public long CrashResumeUntilUtcTicks { get; set; }

    // Additive bootstrap for databases created before this projection existed.
    // All entity reads and writes continue through the owning compiled EF model.
    public static Task EnsureTableAsync(DbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS OwnerRetention (
            TenantId TEXT NOT NULL, OwnerKind TEXT NOT NULL, OwnerId TEXT NOT NULL,
            Revision INTEGER NOT NULL, Phase TEXT NOT NULL, CheckpointUtcTicks INTEGER NOT NULL,
            DeadlineUtcTicks INTEGER NULL, CrashResumeUntilUtcTicks INTEGER NOT NULL,
            PRIMARY KEY (TenantId, OwnerKind, OwnerId));
        CREATE INDEX IF NOT EXISTS IX_OwnerRetention_TenantId_Phase_DeadlineUtcTicks
            ON OwnerRetention (TenantId, Phase, DeadlineUtcTicks);
        """, ct);

    public static void Project(DbSet<OwnerRetentionIndex> rows, OwnerRetentionIndex? row, string tenant, string kind,
        string id, long revision, OwnerRetention retention)
    {
        if (row is null)
        {
            row = new() { TenantId = tenant, OwnerKind = kind, OwnerId = id };
            rows.Add(row);
        }
        row.Revision = revision;
        row.Phase = retention.Phase;
        row.CheckpointUtcTicks = retention.CheckpointUtc.UtcTicks;
        row.DeadlineUtcTicks = retention.DeadlineUtc?.UtcTicks;
        row.CrashResumeUntilUtcTicks = retention.CrashResumeUntilUtc.UtcTicks;
    }
}
