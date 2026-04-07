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
        _workspaceRootPath = DiscoverWorkspaceRoot(_contentRootPath);
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
        if (!allowedRoots.Any(root => IsPathWithinRoot(fullPath, root)))
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
    {
        var configured = string.IsNullOrWhiteSpace(_settings.DefaultWorkingDirectory)
            ? "GnOuGo"
            : _settings.DefaultWorkingDirectory.Trim();

        var desktopPath = ResolveDesktopDirectory();
        var resolved = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(desktopPath, configured));

        if (!Path.IsPathRooted(configured) && !IsPathWithinRoot(resolved, desktopPath))
            throw new InvalidOperationException(
                $"Default working directory '{configured}' must stay within '{desktopPath}'.");

        Directory.CreateDirectory(resolved);
        return resolved;
    }

    internal static string? DiscoverWorkspaceRoot(string contentRootPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(contentRootPath));
        while (current is not null)
        {
            if (current.GetFiles("*.sln").Length != 0 || Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    internal static bool IsPathWithinRoot(string path, string root)
    {
        var np = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 + Path.DirectorySeparatorChar;
        var nr = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 + Path.DirectorySeparatorChar;
        return np.StartsWith(nr, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDesktopDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop)) return Path.GetFullPath(desktop);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) return Path.GetFullPath(Path.Combine(profile, "Desktop"));

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home)) return Path.GetFullPath(Path.Combine(home, "Desktop"));

        throw new InvalidOperationException("Unable to resolve the current user's Desktop directory.");
    }

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

