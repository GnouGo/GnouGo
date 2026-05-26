using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Git.Mcp;

public sealed class GitPolicy
{
    private readonly GitServerSettings _settings;
    private readonly string _contentRootPath;
    private readonly string? _workspaceRootPath;
    private readonly string? _desktopDirectoryPath;
    private readonly string _defaultWorkingDirectory;

    public GitPolicy(IOptions<GitServerSettings> settings)
        : this(settings.Value, AppContext.BaseDirectory)
    {
    }

    public GitPolicy(GitServerSettings settings, string contentRootPath)
        : this(settings, contentRootPath, desktopDirectoryPath: null)
    {
    }

    internal GitPolicy(GitServerSettings settings, string contentRootPath, string? desktopDirectoryPath)
    {
        _settings = settings;
        _contentRootPath = Path.GetFullPath(contentRootPath);
        _workspaceRootPath = GnOuGoWorkspace.DiscoverWorkspaceRoot(_contentRootPath);
        _desktopDirectoryPath = string.IsNullOrWhiteSpace(desktopDirectoryPath) ? null : Path.GetFullPath(desktopDirectoryPath);
        _defaultWorkingDirectory = ResolveDefaultWorkingDirectory();
    }

    public string DefaultWorkingDirectory => _defaultWorkingDirectory;

    public GitPolicyInfo DescribePolicy()
        => new(
            DefaultWorkingDirectory: _defaultWorkingDirectory,
            AllowedWorkingRoots: ResolveAllowedWorkingRoots(),
            AllowMutations: _settings.AllowMutations,
            AllowNetworkOperations: _settings.AllowNetworkOperations,
            RequireCleanWorkingTreeForMerge: _settings.RequireCleanWorkingTreeForMerge,
            MaxDiffCharacters: _settings.MaxDiffCharacters,
            MaxLogCount: _settings.MaxLogCount,
            DefaultRemoteName: _settings.DefaultRemoteName,
            HasConfiguredToken: !string.IsNullOrWhiteSpace(ResolveGitToken()),
            TokenEnvironmentVariables: _settings.TokenEnvironmentVariables);

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

    public string ResolveCloneTargetDirectory(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException("targetDirectory must not be empty.");

        var path = ResolvePath(targetDirectory, mustExist: false, expectDirectory: true, relativeBasePath: _defaultWorkingDirectory);
        if (File.Exists(path))
            throw new InvalidOperationException($"Clone target '{path}' is an existing file.");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException($"Clone target directory '{path}' already exists and is not empty.");
        return path;
    }

    public string? ResolveGitToken()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Token))
            return _settings.Token;

        foreach (var variable in _settings.TokenEnvironmentVariables.Where(static v => !string.IsNullOrWhiteSpace(v)))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public void EnsureGitMutationsAllowed(string operation)
    {
        if (!_settings.AllowMutations)
            throw new InvalidOperationException($"Git mutation '{operation}' is disabled by policy. Set Git:AllowMutations=true to enable it.");
    }

    public void EnsureGitNetworkAllowed(string operation)
    {
        if (!_settings.AllowNetworkOperations)
            throw new InvalidOperationException($"Git network operation '{operation}' is disabled by policy. Set Git:AllowNetworkOperations=true to enable it.");
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
        => GnOuGoWorkspace.DiscoverWorkspaceRoot(contentRootPath);

    internal static bool IsPathWithinRoot(string path, string root)
        => GnOuGoWorkspace.IsPathWithinRoot(path, root);

    private string ResolveDefaultWorkingDirectory()
    {
        var configuredPath = string.IsNullOrWhiteSpace(_settings.DefaultWorkingDirectory)
            ? "GnOuGo"
            : _settings.DefaultWorkingDirectory.Trim();
        var desktopPath = _desktopDirectoryPath ?? GnOuGoWorkspace.ResolveDesktopDirectory();

        var resolvedPath = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(desktopPath, configuredPath));

        if (!Path.IsPathRooted(configuredPath) && !GnOuGoWorkspace.IsPathWithinRoot(resolvedPath, desktopPath))
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

    private void EnsureWithinAllowedRoots(string path)
    {
        var roots = ResolveAllowedWorkingRoots();
        if (!roots.Any(root => IsPathWithinRoot(path, root)))
            throw new InvalidOperationException($"Path '{path}' is outside the allowed roots: {string.Join(", ", roots)}.");
    }

    private static bool ContainsParentTraversalSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));
}
