namespace GnOuGo.Document.Mcp;

/// <summary>
/// Configuration section "Document" bound from appsettings.json.
/// </summary>
public sealed class DocumentServerSettings
{
    public const string SectionName = "Document";

    /// <summary>
    /// Relative (resolved against Desktop) or absolute path used as the default working root.
    /// </summary>
    public string DefaultWorkingDirectory { get; set; } = "GnOuGo";

    /// <summary>
    /// Maximum allowed file size in bytes (default 50 MB).
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// File extensions the server is allowed to read/write (case-insensitive).
    /// </summary>
    public List<string> AllowedExtensions { get; set; } =
    [
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml"
    ];

    /// <summary>
    /// Additional allowed working roots (absolute or relative to workspace).
    /// </summary>
    public List<string> AllowedWorkingRoots { get; set; } = [];
}

