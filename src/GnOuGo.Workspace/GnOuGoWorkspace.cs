namespace GnOuGo.Workspace;

/// <summary>
/// Centralizes workspace directory resolution logic shared across all GnOuGo components.
/// All methods are static, pure, AOT-compatible, and safe for sandboxed environments.
/// </summary>
public static class GnOuGoWorkspace
{
    /// <summary>
    /// Default subfolder name used under the Desktop directory when no explicit path is configured.
    /// </summary>
    public const string DefaultSubfolder = "GnOuGo";

    /// <summary>
    /// Hidden folder used under the GnOuGo workspace for local data.
    /// </summary>
    public const string WorkspaceDataSubfolder = ".GnOuGo";

    /// <summary>
    /// Resolves the current user's Desktop directory, with robust fallback for
    /// Native AOT, sandboxed, and headless environments.
    /// </summary>
    /// <returns>Absolute path to the Desktop directory.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Desktop directory cannot be determined by any strategy.
    /// </exception>
    public static string ResolveDesktopDirectory()
    {
        // Strategy 1: Environment.SpecialFolder.DesktopDirectory
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktopPath))
                return Path.GetFullPath(desktopPath);
        }
        catch
        {
            // GetFolderPath can throw on some Native AOT / sandboxed configurations.
        }

        // Strategy 2: UserProfile/Desktop
        try
        {
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfilePath))
                return Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));
        }
        catch
        {
            // Fallthrough to HOME-based resolution.
        }

        // Strategy 3: HOME environment variable
        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(Path.Combine(homePath, "Desktop"));

        throw new InvalidOperationException(
            "Unable to resolve the current user's Desktop directory. " +
            "Set the HOME environment variable or configure an absolute path.");
    }

    /// <summary>
    /// Resolves the default GnOuGo working directory (Desktop/GnOuGo by default).
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <param name="configuredPath">
    /// Optional configured path. If null/empty, defaults to <c>GnOuGo</c> under the Desktop.
    /// If relative, resolved relative to the Desktop directory.
    /// If absolute, used as-is.
    /// </param>
    /// <returns>Absolute path to the resolved working directory.</returns>
    public static string ResolveDefaultWorkingDirectory(string? configuredPath = null)
    {
        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultSubfolder
            : configuredPath.Trim();

        if (Path.IsPathRooted(normalized))
        {
            var absolute = Path.GetFullPath(normalized);
            Directory.CreateDirectory(absolute);
            return absolute;
        }

        var desktopPath = ResolveDesktopDirectory();
        var resolvedPath = Path.GetFullPath(Path.Combine(desktopPath, normalized));

        if (!IsPathWithinRoot(resolvedPath, desktopPath))
        {
            throw new InvalidOperationException(
                $"Default working directory '{normalized}' must stay within the current user's Desktop directory '{desktopPath}'.");
        }

        Directory.CreateDirectory(resolvedPath);
        return resolvedPath;
    }

    /// <summary>
    /// Safe variant of <see cref="ResolveDefaultWorkingDirectory"/> that catches exceptions
    /// and falls back to a writable location. Ideal for MCP servers running in AOT bundles.
    /// </summary>
    /// <param name="configuredPath">Optional configured path.</param>
    /// <param name="contentRootPath">Fallback base directory (typically <c>AppContext.BaseDirectory</c>).</param>
    /// <returns>Absolute path to a usable working directory.</returns>
    public static string ResolveDefaultWorkingDirectorySafe(string? configuredPath = null, string? contentRootPath = null)
    {
        try
        {
            return ResolveDefaultWorkingDirectory(configuredPath);
        }
        catch
        {
            return ResolveWorkingDirectoryFallback(contentRootPath);
        }
    }

    /// <summary>
    /// Resolves a database file path using the GnOuGo data convention.
    /// Relative paths are resolved from the default GnOuGo working directory,
    /// which remains <c>Desktop/GnOuGo</c> unless explicitly configured.
    /// </summary>
    /// <param name="configuredPath">Configured database path (absolute, relative, or null).</param>
    /// <param name="baseDirectory">Fallback base directory when Desktop-based resolution is unavailable.</param>
    /// <param name="defaultRelativePath">Default relative path when <paramref name="configuredPath"/> is null/empty (e.g. <c>.GnOuGo/data/gnougo-agent.db</c>).</param>
    /// <returns>Absolute path to the database file.</returns>
    public static string ResolveDatabasePath(string? configuredPath, string baseDirectory, string defaultRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return configuredPath;

        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? defaultRelativePath
            : configuredPath.Replace('\\', '/').Trim();

        return Path.GetFullPath(Path.Combine(
            ResolveDefaultWorkingDirectorySafe(contentRootPath: baseDirectory),
            normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Resolves the hidden GnOuGo data root under the default working directory.
    /// </summary>
    public static string ResolveWorkspaceDataDirectory(string baseDirectory)
        => Path.GetFullPath(Path.Combine(
            ResolveDefaultWorkingDirectorySafe(contentRootPath: baseDirectory),
            WorkspaceDataSubfolder));

    /// <summary>
    /// Walks up the directory tree from <paramref name="startPath"/> looking for a <c>.sln</c> file
    /// or <c>.git</c> directory, which indicates the workspace root.
    /// </summary>
    /// <param name="startPath">Starting directory for the upward search.</param>
    /// <returns>Workspace root path, or <c>null</c> if not found.</returns>
    public static string? DiscoverWorkspaceRoot(string startPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current is not null)
        {
            if (current.GetFiles("*.sln").Length != 0 || Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Checks whether <paramref name="path"/> is inside (or equal to) <paramref name="root"/>.
    /// Comparison is case-insensitive and handles trailing separators.
    /// </summary>
    public static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts an absolute path under a workspace root to a portable slash-separated
    /// relative path that can be safely reused in MCP workflow requests.
    /// </summary>
    public static string? ToWorkspaceRelativePath(string? path, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(workspaceRoot);
        if (!IsPathWithinRoot(fullPath, fullRoot))
            return null;

        return NormalizePortablePath(Path.GetRelativePath(fullRoot, fullPath));
    }

    /// <summary>
    /// Normalizes directory separators for workflow-facing path values.
    /// </summary>
    public static string NormalizePortablePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    /// <summary>
    /// Fallback working directory resolution when Desktop-based resolution fails.
    /// Tries HOME/GnOuGo, TMPDIR/GnOuGo, system temp, then contentRootPath/workspace.
    /// </summary>
    internal static string ResolveWorkingDirectoryFallback(string? contentRootPath)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
                ? Path.Combine(home, DefaultSubfolder)
                : null,
            Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } tmp
                ? Path.Combine(tmp, DefaultSubfolder)
                : null,
            Path.Combine(Path.GetTempPath(), DefaultSubfolder),
            contentRootPath is { Length: > 0 }
                ? Path.Combine(contentRootPath, "workspace")
                : null
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                Directory.CreateDirectory(candidate);
                return Path.GetFullPath(candidate);
            }
            catch
            {
                // Try next candidate.
            }
        }

        // Absolute last resort.
        return contentRootPath ?? Path.GetTempPath();
    }
}
