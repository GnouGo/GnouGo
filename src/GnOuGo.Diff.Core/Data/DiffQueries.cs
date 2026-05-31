using GnOuGo.Diff.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Diff.Core.Data;

/// <summary>
/// Precompiled EF Core queries for optimal performance and AOT friendliness.
/// </summary>
internal static class DiffQueries
{
    public static readonly Func<DiffDbContext, string, string, Task<DiffEntry?>> GetLatestRevision =
        EF.CompileAsyncQuery(
            (DiffDbContext db, string entityType, string entityId) =>
                db.DiffEntries
                    .Where(e => e.EntityType == entityType && e.EntityId == entityId)
                    .OrderByDescending(e => e.TimestampTicks)
                    .FirstOrDefault());

    public static readonly Func<DiffDbContext, string, string, long, Task<DiffEntry?>> GetRevisionAtTimestamp =
        EF.CompileAsyncQuery(
            (DiffDbContext db, string entityType, string entityId, long timestampTicks) =>
                db.DiffEntries
                    .Where(e => e.EntityType == entityType && e.EntityId == entityId && e.TimestampTicks <= timestampTicks)
                    .OrderByDescending(e => e.TimestampTicks)
                    .FirstOrDefault());

    public static readonly Func<DiffDbContext, Guid, Task<DiffEntry?>> GetRevisionById =
        EF.CompileAsyncQuery(
            (DiffDbContext db, Guid id) =>
                db.DiffEntries.FirstOrDefault(e => e.Id == id));

    public static readonly Func<DiffDbContext, string, string, IAsyncEnumerable<DiffEntry>> GetRevisionsByEntity =
        EF.CompileAsyncQuery(
            (DiffDbContext db, string entityType, string entityId) =>
                db.DiffEntries
                    .Where(e => e.EntityType == entityType && e.EntityId == entityId)
                    .OrderByDescending(e => e.TimestampTicks)
                    .AsQueryable());

    public static readonly Func<DiffDbContext, string, IAsyncEnumerable<DiffEntry>> GetEntriesByType =
        EF.CompileAsyncQuery(
            (DiffDbContext db, string entityType) =>
                db.DiffEntries.Where(e => e.EntityType == entityType).AsQueryable());
}


