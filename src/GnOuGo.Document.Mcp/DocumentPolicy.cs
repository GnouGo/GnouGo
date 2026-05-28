using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Document.Mcp;

/// <summary>
/// Enforces path security: all file operations must stay inside allowed roots.
/// </summary>
public sealed class DocumentPolicy
{
    private readonly DocumentServerSettings _settings;
    private readonly string _contentRootPath;
    private readonly string? _workspaceRootPath;
    private readonly string _defaultWorkingDirectory;

    public DocumentPolicy(IOptions<DocumentServerSettings> settings)
        : this(settings.Value, AppContext.BaseDirectory)
    {
    }

    public DocumentPolicy(DocumentServerSettings settings, string contentRootPath)
    {
        _settings = settings;
        _contentRootPath = Path.GetFullPath(contentRootPath);
        _workspaceRootPath = GnOuGoWorkspace.DiscoverWorkspaceRoot(_contentRootPath);
        _defaultWorkingDirectory = ResolveDefaultWorkingDirectory();
    }

    public string DefaultWorkingDirectory => _defaultWorkingDirectory;
    public long MaxFileSizeBytes => _settings.MaxFileSizeBytes;

    /// <summary>
    /// Resolve a file path (relative or absolute) and ensure it is inside an allowed root.
    /// </summary>
    public string ResolveFilePath(string filePath)
    {
        var normalized = NormalizeRequired(filePath, nameof(filePath));
        var fullPath = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(_defaultWorkingDirectory, normalized));

        var allowedRoots = ResolveAllowedRoots();
        if (!allowedRoots.Any(root => GnOuGoWorkspace.IsPathWithinRoot(fullPath, root)))
            throw new InvalidOperationException(
                $"Path '{fullPath}' is outside allowed roots: {string.Join(", ", allowedRoots)}.");

        return fullPath;
    }

    /// <summary>
    /// Check whether the file extension is in the allow list.
    /// </summary>
    public bool IsExtensionAllowed(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return _settings.AllowedExtensions
            .Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a summary of the current policy.
    /// </summary>
    public DocumentPolicyInfo DescribePolicy()
        => new(
            DefaultWorkingDirectory: _defaultWorkingDirectory,
            AllowedRoots: [.. ResolveAllowedRoots()],
            AllowedExtensions: [.. _settings.AllowedExtensions.Distinct(StringComparer.OrdinalIgnoreCase)],
            MaxFileSizeBytes: _settings.MaxFileSizeBytes);

    // ── Private helpers ─────────────────────────────────────────

    private List<string> ResolveAllowedRoots()
    {
        var basePath = _workspaceRootPath ?? _contentRootPath;
        var roots = new List<string> { _defaultWorkingDirectory };
        roots.AddRange(_settings.AllowedWorkingRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(basePath, p))));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string ResolveDefaultWorkingDirectory()
        => GnOuGoWorkspace.ResolveDefaultWorkingDirectory(_settings.DefaultWorkingDirectory);

    internal static string? DiscoverWorkspaceRoot(string contentRootPath)
        => GnOuGoWorkspace.DiscoverWorkspaceRoot(contentRootPath);

    internal static bool IsPathWithinRoot(string path, string root)
        => GnOuGoWorkspace.IsPathWithinRoot(path, root);

    private static string NormalizeRequired(string? value, string param)
    {
        var v = value?.Trim();
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"{param} must not be empty.");
        return v;
    }
}

public sealed record DocumentPolicyInfo(
    string DefaultWorkingDirectory,
    IReadOnlyList<string> AllowedRoots,
    IReadOnlyList<string> AllowedExtensions,
    long MaxFileSizeBytes);
