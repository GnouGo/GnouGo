namespace GnOuGo.DocIngestor.Mcp.Services;

public sealed class OriginalDocumentStore
{
    private readonly string _rootDirectory;

    public OriginalDocumentStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public async Task<string> SaveAsync(string tenantId, string collection, string documentId, string fileName, string sourcePath, CancellationToken ct = default)
    {
        var tenantSegment = SanitizePathSegment(tenantId);
        var collectionSegment = SanitizePathSegment(collection);
        var ext = Path.GetExtension(fileName);
        var targetDirectory = Path.Combine(_rootDirectory, tenantSegment, collectionSegment);
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(targetDirectory, SanitizePathSegment(documentId) + ext);
        await using var input = File.OpenRead(sourcePath);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, ct);
        return targetPath;
    }

    public Task DeleteAsync(string? originalPath, CancellationToken ct = default)
    {
        _ = ct;
        if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
            File.Delete(originalPath);

        return Task.CompletedTask;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) || ch is ':' or '/' or '\\' ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }
}

