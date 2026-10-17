using GnOuGo.Flow.Planning;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>OS-owned leases share the index's lifetime and survive coordinator restarts without expiring active work.</summary>
internal static class PlanningSessionLease
{
    internal static async Task<FileStream?> TryAcquireAsync(IDbContextFactory<PlanningDbContext> contexts,
        string tenant, string session, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var database = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        var directory = database + ".leases";
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PlanningGraphCompiler.Fingerprint(tenant + "\n" + session) + ".lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (File.Exists(path)) { return null; }
        // Never unlink a lock file: another process could still hold its inode.
    }
}
