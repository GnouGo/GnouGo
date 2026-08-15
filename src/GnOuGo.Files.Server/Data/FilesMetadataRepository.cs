using GnOuGo.Files.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Files.Server.Data;

public sealed class FilesMetadataRepository
{
    private readonly FilesDbContext _db;

    public FilesMetadataRepository(FilesDbContext db)
    {
        _db = db;
    }

    public async Task InsertAsync(FileRecord record, CancellationToken cancellationToken)
    {
        _db.Files.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<FileRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var db = _db;
        var fileId = id;
        var token = cancellationToken;
        var snapshot = await db.Files
            .Where(file => file.Id == fileId)
            .Select(file => new FileRecordSnapshot(
                file.Id,
                file.TenantId,
                file.OriginalFileName,
                file.ContentType,
                file.StoredFileName,
                file.StoredPath,
                file.SizeBytes,
                file.CreatedUtc,
                file.ExpiresUtc))
            .FirstOrDefaultAsync(token);
        return snapshot?.ToFileRecord();
    }

    public async Task<FileRecord?> GetAsync(string id, string tenantId, CancellationToken cancellationToken)
    {
        var db = _db;
        var fileId = id;
        var tenant = tenantId;
        var token = cancellationToken;
        var snapshot = await db.Files
            .Where(file => file.Id == fileId && file.TenantId == tenant)
            .Select(file => new FileRecordSnapshot(
                file.Id,
                file.TenantId,
                file.OriginalFileName,
                file.ContentType,
                file.StoredFileName,
                file.StoredPath,
                file.SizeBytes,
                file.CreatedUtc,
                file.ExpiresUtc))
            .FirstOrDefaultAsync(token);
        return snapshot?.ToFileRecord();
    }

    public async Task<List<FileRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var db = _db;
        var token = cancellationToken;
        var snapshots = await db.Files
            .Select(file => new FileRecordSnapshot(
                file.Id,
                file.TenantId,
                file.OriginalFileName,
                file.ContentType,
                file.StoredFileName,
                file.StoredPath,
                file.SizeBytes,
                file.CreatedUtc,
                file.ExpiresUtc))
            .ToListAsync(token);
        return snapshots.ConvertAll(snapshot => snapshot.ToFileRecord());
    }

    public async Task<List<FileRecord>> ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        var db = _db;
        var tenant = tenantId;
        var token = cancellationToken;
        var snapshots = await db.Files
            .Where(file => file.TenantId == tenant)
            .Select(file => new FileRecordSnapshot(
                file.Id,
                file.TenantId,
                file.OriginalFileName,
                file.ContentType,
                file.StoredFileName,
                file.StoredPath,
                file.SizeBytes,
                file.CreatedUtc,
                file.ExpiresUtc))
            .ToListAsync(token);
        return snapshots.ConvertAll(snapshot => snapshot.ToFileRecord());
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var db = _db;
        var fileId = id;
        var token = cancellationToken;
        var existingId = await db.Files
            .Where(file => file.Id == fileId)
            .Select(file => file.Id)
            .FirstOrDefaultAsync(token);
        if (existingId is not null)
        {
            var entity = _db.Files.Local.FirstOrDefault(file => file.Id == existingId);
            if (entity is null)
            {
                entity = new FileRecord { Id = existingId };
                _db.Files.Attach(entity);
            }

            _db.Files.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

}

internal sealed record FileRecordSnapshot(
    string Id,
    string TenantId,
    string OriginalFileName,
    string ContentType,
    string StoredFileName,
    string StoredPath,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc)
{
    public FileRecord ToFileRecord()
        => new()
        {
            Id = Id,
            TenantId = TenantId,
            OriginalFileName = OriginalFileName,
            ContentType = ContentType,
            StoredFileName = StoredFileName,
            StoredPath = StoredPath,
            SizeBytes = SizeBytes,
            CreatedUtc = CreatedUtc,
            ExpiresUtc = ExpiresUtc
        };
}
