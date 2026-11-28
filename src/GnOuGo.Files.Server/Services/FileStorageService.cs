using GnOuGo.Files.Core;
using GnOuGo.Files.Server.Data;
using GnOuGo.Files.Server.Models;
using GnOuGo.Files.Server.Options;
using GnOuGo.Files.Server.Web;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Mime;

namespace GnOuGo.Files.Server.Services;

public sealed class FileStorageService
{
    private readonly DurableFileStore _files;
    private readonly FilesMetadataRepository _metadata;
    private readonly FilesServerOptions _options;
    private readonly FilesStoragePaths _paths;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(
        FilesMetadataRepository metadata,
        IOptions<FilesServerOptions> options,
        FilesStoragePaths paths,
        DurableFileStore files,
        ILogger<FileStorageService> logger)
    {
        _metadata = metadata;
        _files = files;
        _options = options.Value;
        _paths = paths;
        _logger = logger;
    }

    public async Task<FileUploadResponse> UploadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var ttl = ResolveRequestedTtl(context.Request);
        var now = DateTimeOffset.UtcNow;
        var tenant = ResolveTenantId(context.Request) ?? throw new ArgumentException("Invalid upload tenant.");
        var expires = now.Add(ttl);
        var file = await _files.UploadAsync(tenant, context.Request.Body,
            context.Request.ContentType ?? MediaTypeNames.Application.Octet, ResolveFileName(context.Request), expires, cancellationToken);
        var record = new FileRecord
        {
            Id = file.Id, TenantId = tenant, StoredFileName = "encrypted-v2",
            CreatedUtc = now, ExpiresUtc = expires, SizeBytes = file.Size
        };
        await _metadata.InsertAsync(record, cancellationToken);
        return ToUploadResponse(Describe(record, file), context.Request, now);
    }

    private static FileRecord Describe(FileRecord record, StoredFile file)
    {
        record.ContentType = file.ContentType;
        record.OriginalFileName = file.Name;
        return record;
    }

    public async Task<FileRecord?> GetAvailableFileAsync(string id, HttpRequest request, CancellationToken ct)
    {
        if (ResolveTenantId(request) is not { } tenant) return null;
        var record = await _metadata.GetAsync(id, tenant, ct);
        if (record?.StoredFileName != "encrypted-v2" || record.ExpiresUtc <= DateTimeOffset.UtcNow) return null;
        var file = await _files.InspectUploadAsync(tenant, id, ct);
        return file is null ? null : Describe(record, file);
    }

    public Task<bool> DownloadAsync(FileRecord record, Stream destination, CancellationToken ct) =>
        _files.CopyAsync(record.TenantId, record.Id, null, destination, ct);

    public async Task<FileListResponse> ListAvailableAsync(HttpRequest request, CancellationToken ct)
    {
        if (ResolveTenantId(request) is not { } tenant) return new([]);
        var now = DateTimeOffset.UtcNow;
        var rows = await _metadata.ListAsync(tenant, ct);
        var files = new List<FileListItemResponse>();
        foreach (var row in rows.Where(f => f.StoredFileName == "encrypted-v2" && f.ExpiresUtc > now).OrderBy(f => f.ExpiresUtc))
        {
            if (await _files.InspectUploadAsync(tenant, row.Id, ct) is not { } file) continue;
            files.Add(new(row.Id, tenant, file.Name, file.ContentType, file.Size, row.CreatedUtc, row.ExpiresUtc,
                Math.Max(0, (long)(row.ExpiresUtc - now).TotalSeconds), FilesLinks.DownloadUrl(_options.PublicBaseUrl, request, row.Id)));
        }
        return new(files);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        await _files.SweepAsync(ct);
        var rows = await _metadata.ListAsync(ct);
        var deleted = 0;
        foreach (var row in rows.Where(f => f.StoredFileName != "encrypted-v2" || f.ExpiresUtc <= DateTimeOffset.UtcNow))
        {
            // Historical plaintext uploads had an exact ID-derived basename. Ignore the stored absolute path.
            if (row.Id.Length == 32 && row.Id.All(char.IsAsciiHexDigitLower) && row.StoredFileName == row.Id + ".blob")
                File.Delete(Path.Combine(_paths.StorageRootPath, row.StoredFileName));
            await _metadata.DeleteAsync(row.Id, ct);
            deleted++;
        }
        if (Directory.Exists(_paths.StorageRootPath))
        {
            // Retire orphaned plaintext uploads from the removed writer as well as indexed uploads.
            foreach (var path in Directory.EnumerateFiles(_paths.StorageRootPath, "*.blob", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(path);
                if (id.Length == 32 && id.All(char.IsAsciiHexDigitLower)) File.Delete(path);
            }
        }
        if (deleted > 0) _logger.LogInformation("Purged {DeletedCount} temporary file records.", deleted);
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

    private string? ResolveTenantId(HttpRequest request)
    {
        var tenant = request.HttpContext.User.Identity?.IsAuthenticated == true
            ? request.HttpContext.User.FindFirst("tenant_id")?.Value ?? _options.TenantId : _options.TenantId;
        var requested = request.Headers["X-Tenant-Id"].FirstOrDefault() ?? request.Query["tenantId"].FirstOrDefault();
        return requested is null || requested == tenant ? tenant : null;
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

    private FileUploadResponse ToUploadResponse(FileRecord record, HttpRequest request, DateTimeOffset now)
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
            FilesLinks.DownloadUrl(_options.PublicBaseUrl, request, record.Id));
    }
}
