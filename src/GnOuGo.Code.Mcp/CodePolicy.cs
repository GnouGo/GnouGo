using Microsoft.Extensions.Options;

namespace GnOuGo.Code.Mcp;

public sealed class CodePolicy
{
    private readonly CodeServerSettings _settings;
    private readonly string _contentRootPath;
    private readonly string? _workspaceRootPath;
    private readonly string _defaultWorkingDirectory;

    public CodePolicy(IOptions<CodeServerSettings> settings)
        : this(settings.Value, AppContext.BaseDirectory)
    {
    }

    public CodePolicy(CodeServerSettings settings, string contentRootPath)
    {
        _settings = settings;
        _contentRootPath = Path.GetFullPath(contentRootPath);
        _workspaceRootPath = DiscoverWorkspaceRoot(_contentRootPath);
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
            HasConfiguredToken: !string.IsNullOrWhiteSpace(ResolveConfiguredToken()),
            TokenEnvironmentVariables: _settings.Copilot.TokenEnvironmentVariables,
            Git: new CodeGitPolicyInfo(
                AllowMutations: _settings.Git.AllowMutations,
                AllowNetworkOperations: _settings.Git.AllowNetworkOperations,
                RequireCleanWorkingTreeForMerge: _settings.Git.RequireCleanWorkingTreeForMerge,
                MaxDiffCharacters: _settings.Git.MaxDiffCharacters,
                MaxLogCount: _settings.Git.MaxLogCount,
                DefaultRemoteName: _settings.Git.DefaultRemoteName,
                HasConfiguredToken: !string.IsNullOrWhiteSpace(ResolveGitToken()),
                TokenEnvironmentVariables: _settings.Git.TokenEnvironmentVariables));

    public string ResolveProjectRoot(string? projectRoot)
    {
        var candidate = string.IsNullOrWhiteSpace(projectRoot)
            ? _defaultWorkingDirectory
            : ResolvePath(projectRoot, mustExist: true, expectDirectory: true);

        if (!Directory.Exists(candidate))
            throw new InvalidOperationException($"Project root '{candidate}' does not exist.");
        EnsureWithinAllowedRoots(candidate);
        return candidate;
    }

    public string ResolveGitCloneTargetDirectory(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException("targetDirectory must not be empty.");

        var path = ResolvePath(targetDirectory, mustExist: false, expectDirectory: true);
        if (File.Exists(path))
            throw new InvalidOperationException($"Clone target '{path}' is an existing file.");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException($"Clone target directory '{path}' already exists and is not empty.");
        return path;
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

    public string? ResolveGitToken()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Git.Token))
            return _settings.Git.Token;

        foreach (var variable in _settings.Git.TokenEnvironmentVariables.Where(static v => !string.IsNullOrWhiteSpace(v)))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return ResolveConfiguredToken();
    }

    public void EnsureGitMutationsAllowed(string operation)
    {
        if (!_settings.Git.AllowMutations)
            throw new InvalidOperationException($"Git mutation '{operation}' is disabled by policy. Set Code:Git:AllowMutations=true to enable it.");
    }

    public void EnsureGitNetworkAllowed(string operation)
    {
        if (!_settings.Git.AllowNetworkOperations)
            throw new InvalidOperationException($"Git network operation '{operation}' is disabled by policy. Set Code:Git:AllowNetworkOperations=true to enable it.");
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
            ? _workspaceRootPath ?? _contentRootPath
            : _settings.DefaultWorkingDirectory.Trim();
        var basePath = _workspaceRootPath ?? _contentRootPath;
        var resolved = Path.GetFullPath(Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(basePath, configuredPath));
        Directory.CreateDirectory(resolved);
        EnsureWithinAllowedRoots(resolved, includeDefault: false);
        return resolved;
    }

    private string ResolvePath(string configuredPath, bool mustExist, bool expectDirectory)
    {
        if (ContainsParentTraversalSegment(configuredPath) || configuredPath.IndexOfAny(['*', '?']) >= 0)
            throw new InvalidOperationException("Paths must not contain parent traversal segments or wildcard characters.");

        var basePath = _workspaceRootPath ?? _contentRootPath;
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

    private static bool HasDriveRelativePrefix(string path)
        => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}



