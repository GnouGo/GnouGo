using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

public sealed class CodePolicy
{
    private readonly CodeServerSettings _settings;
    private readonly string _contentRootPath;
    private readonly string? _workspaceRootPath;
    private readonly string? _desktopDirectoryPath;
    private readonly string _defaultWorkingDirectory;

    public CodePolicy(IOptions<CodeServerSettings> settings)
        : this(settings.Value, AppContext.BaseDirectory)
    {
    }

    public CodePolicy(CodeServerSettings settings, string contentRootPath)
        : this(settings, contentRootPath, desktopDirectoryPath: null)
    {
    }

    internal CodePolicy(CodeServerSettings settings, string contentRootPath, string? desktopDirectoryPath)
    {
        _settings = settings;
        _contentRootPath = Path.GetFullPath(contentRootPath);
        _workspaceRootPath = DiscoverWorkspaceRoot(_contentRootPath);
        _desktopDirectoryPath = string.IsNullOrWhiteSpace(desktopDirectoryPath) ? null : Path.GetFullPath(desktopDirectoryPath);
        _defaultWorkingDirectory = ResolveDefaultWorkingDirectory();
    }

    public string DefaultWorkingDirectory => _defaultWorkingDirectory;

    public CodePolicyInfo DescribePolicy()
        => new(
            DefaultWorkingDirectory: _defaultWorkingDirectory,
            AllowedWorkingRoots: ResolveAllowedWorkingRoots(),
            AllowedExtensions: NormalizeExtensions(_settings.AllowedExtensions),
            MaxFileSizeBytes: _settings.MaxFileSizeBytes,
            MaxSearchResults: _settings.MaxSearchResults,
            MaxPromptCharacters: _settings.MaxPromptCharacters,
            AllowWrites: _settings.AllowWrites,
            CopilotProvider: _settings.Copilot.Provider,
            CopilotModel: _settings.Copilot.Model,
            CopilotMode: GitHubCopilotCodeClient.NormalizeMessageMode(_settings.Copilot.Mode),
            CopilotForwardTraceContext: _settings.Copilot.ForwardTraceContext,
            CopilotTelemetryEnabled: _settings.Copilot.Telemetry.Enabled,
            HasConfiguredToken: !string.IsNullOrWhiteSpace(ResolveConfiguredToken()),
            TokenEnvironmentVariables: _settings.Copilot.TokenEnvironmentVariables);

    public string ResolveProjectRoot(string? projectRoot)
    {
        var candidate = string.IsNullOrWhiteSpace(projectRoot)
            ? _defaultWorkingDirectory
            : ResolvePath(projectRoot, mustExist: true, expectDirectory: true, relativeBasePath: _defaultWorkingDirectory);

        if (!Directory.Exists(candidate))
            throw new InvalidOperationException($"Project root '{candidate}' does not exist.");
        EnsureWithinAllowedRoots(candidate);
        return candidate;
    }

    public string ResolveReadableFile(string projectRoot, string relativePath)
    {
        var resolvedRoot = ResolveProjectRoot(projectRoot);
        var path = ResolveRelativePath(resolvedRoot, relativePath);
        EnsureAllowedFile(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"File '{relativePath}' was not found inside project root.", path);
        var length = new FileInfo(path).Length;
        if (length > _settings.MaxFileSizeBytes)
            throw new InvalidOperationException($"File '{relativePath}' exceeds max size {_settings.MaxFileSizeBytes} bytes.");
        return path;
    }

    public string ResolveWritableFile(string projectRoot, string relativePath)
    {
        if (!_settings.AllowWrites)
            throw new InvalidOperationException("Writes are disabled by policy. Set Code:AllowWrites=true to enable code_write_file.");
        var resolvedRoot = ResolveProjectRoot(projectRoot);
        var path = ResolveRelativePath(resolvedRoot, relativePath);
        EnsureAllowedFile(path);
        return path;
    }

    public string? ResolveConfiguredToken()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Copilot.ApiKey))
            return _settings.Copilot.ApiKey;

        foreach (var variable in _settings.Copilot.TokenEnvironmentVariables.Where(static v => !string.IsNullOrWhiteSpace(v)))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public void EnsurePromptWithinLimit(string value, string parameterName)
    {
        if (value.Length > _settings.MaxPromptCharacters)
            throw new InvalidOperationException($"{parameterName} exceeds max length {_settings.MaxPromptCharacters} characters.");
    }


    internal IReadOnlyList<string> ResolveAllowedWorkingRoots()
    {
        var basePath = _workspaceRootPath ?? _contentRootPath;
        var roots = new List<string> { _defaultWorkingDirectory };
        roots.AddRange(_settings.AllowedWorkingRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path))));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? DiscoverWorkspaceRoot(string contentRootPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(contentRootPath));
        while (current is not null)
        {
            if (current.GetFiles("*.sln").Any() || Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    internal static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveDefaultWorkingDirectory()
    {
        var configuredPath = string.IsNullOrWhiteSpace(_settings.DefaultWorkingDirectory)
            ? "GnOuGo"
            : _settings.DefaultWorkingDirectory.Trim();
        var desktopPath = _desktopDirectoryPath ?? ResolveDesktopDirectory();

        var resolvedPath = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(desktopPath, configuredPath));

        if (!Path.IsPathRooted(configuredPath) && !IsPathWithinRoot(resolvedPath, desktopPath))
        {
            throw new InvalidOperationException(
                $"Default working directory '{configuredPath}' must stay within the current user's Desktop directory '{desktopPath}'.");
        }

        Directory.CreateDirectory(resolvedPath);
        return resolvedPath;
    }

    private string ResolvePath(string configuredPath, bool mustExist, bool expectDirectory, string? relativeBasePath = null)
    {
        if (ContainsParentTraversalSegment(configuredPath) || configuredPath.IndexOfAny(['*', '?']) >= 0)
            throw new InvalidOperationException("Paths must not contain parent traversal segments or wildcard characters.");

        var basePath = relativeBasePath ?? _workspaceRootPath ?? _contentRootPath;
        var path = Path.GetFullPath(Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(basePath, configuredPath));
        EnsureWithinAllowedRoots(path);
        if (mustExist)
        {
            var exists = expectDirectory ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
                throw new InvalidOperationException($"Path '{path}' does not exist.");
        }
        return path;
    }

    private string ResolveRelativePath(string projectRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("relativePath must not be empty.");
        if (Path.IsPathRooted(relativePath) || HasDriveRelativePrefix(relativePath))
            throw new InvalidOperationException("relativePath must be relative to the project root.");
        if (ContainsParentTraversalSegment(relativePath) || relativePath.IndexOfAny(['*', '?']) >= 0)
            throw new InvalidOperationException("relativePath must not contain parent traversal segments or wildcard characters.");

        var path = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        if (!IsPathWithinRoot(path, projectRoot))
            throw new InvalidOperationException("relativePath resolves outside the project root.");
        EnsureWithinAllowedRoots(path);
        return path;
    }

    private void EnsureAllowedFile(string path)
    {
        var extension = Path.GetExtension(path);
        var allowed = NormalizeExtensions(_settings.AllowedExtensions);
        if (allowed.Count > 0 && !allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Extension '{extension}' is not allowed. Allowed: {string.Join(", ", allowed)}.");
    }

    private void EnsureWithinAllowedRoots(string path, bool includeDefault = true)
    {
        var roots = includeDefault ? ResolveAllowedWorkingRoots() : ResolveAllowedWorkingRootsWithoutDefault();
        if (!roots.Any(root => IsPathWithinRoot(path, root)))
            throw new InvalidOperationException($"Path '{path}' is outside the allowed roots: {string.Join(", ", roots)}.");
    }

    private IReadOnlyList<string> ResolveAllowedWorkingRootsWithoutDefault()
    {
        var basePath = _workspaceRootPath ?? _contentRootPath;
        var configured = _settings.AllowedWorkingRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return configured.Length == 0 ? [Path.GetFullPath(_workspaceRootPath ?? _contentRootPath)] : configured;
    }

    private static IReadOnlyList<string> NormalizeExtensions(IEnumerable<string> extensions)
        => extensions
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Select(static e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ContainsParentTraversalSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));

    private static string ResolveDesktopDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
            return Path.GetFullPath(desktopPath);

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            return Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(Path.Combine(homePath, "Desktop"));

        throw new InvalidOperationException("Unable to resolve the current user's Desktop directory.");
    }

    private static bool HasDriveRelativePrefix(string path)
        => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}



