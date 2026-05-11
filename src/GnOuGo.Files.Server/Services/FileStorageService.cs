using GnOuGo.Files.Server.Data;
using GnOuGo.Files.Server.Models;
using GnOuGo.Files.Server.Options;
using GnOuGo.Files.Server.Web;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Mime;
using System.Security.Cryptography;

namespace GnOuGo.Files.Server.Services;

public sealed class FileStorageService
{
    private const string DefaultTenantId = "default";
    private readonly FilesMetadataRepository _metadata;
    private readonly FilesServerOptions _options;
    private readonly FilesStoragePaths _paths;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(
        FilesMetadataRepository metadata,
        IOptions<FilesServerOptions> options,
        FilesStoragePaths paths,
        ILogger<FileStorageService> logger)
    {
        _metadata = metadata;
        _options = options.Value;
        _paths = paths;
        _logger = logger;
    }

    public async Task<FileUploadResponse> UploadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var ttl = ResolveRequestedTtl(context.Request);
        var now = DateTimeOffset.UtcNow;
        var id = CreateId();
        var originalFileName = ResolveFileName(context.Request);
        var contentType = string.IsNullOrWhiteSpace(context.Request.ContentType)
            ? MediaTypeNames.Application.Octet
            : context.Request.ContentType;
        var tenantId = ResolveTenantId(context.Request);
        var storedFileName = id + ".blob";
        var storedPath = Path.Combine(_paths.StorageRootPath, storedFileName);

        Directory.CreateDirectory(_paths.StorageRootPath);

        long sizeBytes = 0;
        try
        {
            await using (var target = new FileStream(
                storedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                _options.GetStreamBufferSize(),
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sizeBytes = await CopyCountingAsync(context.Request.Body, target, _options.GetStreamBufferSize(), cancellationToken);
            }

            var record = new FileRecord
            {
                Id = id,
                TenantId = tenantId,
                OriginalFileName = originalFileName,
                ContentType = contentType,
                StoredFileName = storedFileName,
                StoredPath = storedPath,
                SizeBytes = sizeBytes,
                CreatedUtc = now,
                ExpiresUtc = now.Add(ttl)
            };

            await _metadata.InsertAsync(record, cancellationToken);

            return ToUploadResponse(record, context.Request, now);
        }
        catch
        {
            TryDeleteFile(storedPath, _logger);
            throw;
        }
    }

    public async Task<FileRecord?> GetAvailableFileAsync(string id, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = await _metadata.GetAsync(id, cancellationToken);
        if (record is null)
            return null;

        if (record.ExpiresUtc <= now || !File.Exists(record.StoredPath))
            return null;

        return record;
    }

    public async Task<FileListResponse> ListAvailableAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await _metadata.ListAsync(cancellationToken);

        var files = records
            .Where(file => file.ExpiresUtc > now && File.Exists(file.StoredPath))
            .OrderBy(file => file.ExpiresUtc)
            .Select(file => new FileListItemResponse(
                file.Id,
                file.TenantId,
                file.OriginalFileName,
                file.ContentType,
                file.SizeBytes,
                file.CreatedUtc,
                file.ExpiresUtc,
                Math.Max(0, (long)(file.ExpiresUtc - now).TotalSeconds),
                BuildDownloadUrl(request, file.Id)))
            .ToList();

        return new FileListResponse(files);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await _metadata.ListAsync(cancellationToken);

        var deleted = 0;
        foreach (var file in expired.Where(file => file.ExpiresUtc <= now).OrderBy(file => file.ExpiresUtc))
        {
            TryDeleteFile(file.StoredPath, _logger);
            await _metadata.DeleteAsync(file.Id, cancellationToken);
            deleted++;
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Purged {DeletedCount} expired temporary file(s).", deleted);
        }

        return deleted;
    }

    public static TimeSpan ParseTtl(string? value, TimeSpan defaultTtl)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultTtl;

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var timeSpan) && timeSpan > TimeSpan.Zero)
            return timeSpan;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours > 0)
            return TimeSpan.FromHours(hours);

        throw new ArgumentException("ttl must be a positive TimeSpan such as '12:00:00' or a positive number of hours such as '1.5'.", nameof(value));
    }

    private TimeSpan ResolveRequestedTtl(HttpRequest request)
    {
        return ParseTtl(request.Query["ttl"].FirstOrDefault(), _options.GetDefaultTtl());
    }

    private static string ResolveTenantId(HttpRequest request)
    {
        var tenantId = request.Headers["X-Tenant-Id"].FirstOrDefault()
            ?? request.Query["tenantId"].FirstOrDefault();

        return string.IsNullOrWhiteSpace(tenantId) ? DefaultTenantId : tenantId.Trim();
    }

    private static string ResolveFileName(HttpRequest request)
    {
        var fileName = request.Query["fileName"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fileName) && request.Headers.ContentDisposition.Count > 0)
        {
            try
            {
                var disposition = new ContentDisposition(request.Headers.ContentDisposition.ToString());
                fileName = disposition.FileName;
            }
            catch
            {
                fileName = null;
            }
        }

        fileName = string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName;
        return Path.GetFileName(fileName.Trim().Trim('"'));
    }

    private static async Task<long> CopyCountingAsync(Stream source, Stream target, int bufferSize, CancellationToken cancellationToken)
    {
        var buffer = new byte[bufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        return total;
    }

    private static string CreateId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static FileUploadResponse ToUploadResponse(FileRecord record, HttpRequest request, DateTimeOffset now)
    {
        return new FileUploadResponse(
            record.Id,
            record.TenantId,
            record.OriginalFileName,
            record.ContentType,
            record.SizeBytes,
            record.CreatedUtc,
            record.ExpiresUtc,
            Math.Max(0, (long)(record.ExpiresUtc - now).TotalSeconds),
            BuildDownloadUrl(request, record.Id));
    }

    private static string BuildDownloadUrl(HttpRequest request, string id)
    {
        return $"{request.Scheme}://{request.Host}/api/files/{Uri.EscapeDataString(id)}";
    }

    private static void TryDeleteFile(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Best-effort cleanup failed for stored file '{Path}'.", path);
            // Best-effort cleanup: another process can still hold the file handle.
        }
    }
}




