namespace GnOuGo.Files.Server.Web;

public sealed record ApiErrorResponse(string Error, string Message);

public sealed record HealthStatusResponse(string Status, DateTimeOffset Utc);

public sealed record FileUploadResponse(
    string Id,
    string TenantId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    long TtlSeconds,
    string DownloadUrl);

public sealed record FileListItemResponse(
    string Id,
    string TenantId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    long TtlSecondsRemaining,
    string DownloadUrl);

public sealed record FileListResponse(List<FileListItemResponse> Files);

public sealed record FilesConfigResponse(
    string StorageRootPath,
    string DatabasePath,
    double DefaultTtlHours,
    int PurgeIntervalSeconds);



