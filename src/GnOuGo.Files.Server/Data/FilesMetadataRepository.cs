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
        return await FilesQueries.GetFileById(_db, id);
    }

    public async Task<List<FileRecord>> ListAsync(CancellationToken cancellationToken)
    {
        return await _db.Files.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var entity = await _db.Files.FindAsync([id], cancellationToken);
        if (entity is not null)
        {
            _db.Files.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
