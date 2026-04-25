namespace GnOuGo.Files.Server.Models;

public sealed class FileRecord
{
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = "default";

    public string OriginalFileName { get; set; } = "download.bin";

    public string ContentType { get; set; } = "application/octet-stream";

    public string StoredFileName { get; set; } = string.Empty;

    public string StoredPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ExpiresUtc { get; set; }
}

