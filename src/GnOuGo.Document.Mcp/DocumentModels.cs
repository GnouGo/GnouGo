namespace GnOuGo.Document.Mcp;

// ── Read result ──────────────────────────────────────────────────────────

public sealed record DocumentReadResult(
    bool Success,
    string? FilePath,
    string? Extension,
    IReadOnlyList<DocumentSection> Sections,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static DocumentReadResult Error(string code, string message)
        => new(false, null, null, [], code, message);

    public static DocumentReadResult Ok(string filePath, string extension, IReadOnlyList<DocumentSection> sections)
        => new(true, filePath, extension, sections, null, null);
}

public sealed record DocumentSection(
    string SectionId,
    string Title,
    int? PageNumber,
    string Content);

// ── Write result ─────────────────────────────────────────────────────────

public sealed record DocumentWriteResult(
    bool Success,
    string? FilePath,
    long? BytesWritten,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static DocumentWriteResult Error(string code, string message)
        => new(false, null, null, code, message);
}

// ── List result ──────────────────────────────────────────────────────────

public sealed record DocumentListResult(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<DocumentFileInfo> Files);

public sealed record DocumentFileInfo(
    string RelativePath,
    string FullPath,
    string Extension,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);


