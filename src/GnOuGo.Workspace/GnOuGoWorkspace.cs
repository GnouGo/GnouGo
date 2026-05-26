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
            if (!string.IsNullOrWhiteSpace(desktopPath) && Directory.Exists(desktopPath))
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
            {
                var candidate = Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Fallthrough to HOME-based resolution.
        }

        // Strategy 3: HOME environment variable
        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
        {
            var candidate = Path.GetFullPath(Path.Combine(homePath, "Desktop"));
            if (Directory.Exists(candidate))
                return candidate;
            // Desktop might not exist on headless systems; use HOME directly.
            return Path.GetFullPath(homePath);
        }

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
    /// When the path uses the default <c>data/</c> prefix, it resolves to <c>Desktop/GnOuGo/data/</c>.
    /// </summary>
    /// <param name="configuredPath">Configured database path (absolute, relative, or null).</param>
    /// <param name="baseDirectory">Base directory for non-standard relative paths.</param>
    /// <param name="defaultRelativePath">Default relative path when <paramref name="configuredPath"/> is null/empty (e.g. <c>data/gnougo-agent.db</c>).</param>
    /// <returns>Absolute path to the database file.</returns>
    public static string ResolveDatabasePath(string? configuredPath, string baseDirectory, string defaultRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return configuredPath;

        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? defaultRelativePath
            : configuredPath.Replace('\\', '/').Trim();

        if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, defaultRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(
                ResolveDesktopDirectory(),
                DefaultSubfolder,
                normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath ?? defaultRelativePath));
    }

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

