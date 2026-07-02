using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class LocalProjectSessionFsProvider : SessionFsProvider
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode"
    };

    private readonly CodePolicy _policy;
    private readonly CodeServerSettings _settings;
    private readonly string _projectRoot;
    private readonly ILogger _logger;
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    public LocalProjectSessionFsProvider(
        CodePolicy policy,
        CodeServerSettings settings,
        string projectRoot,
        ILogger logger)
    {
        _policy = policy;
        _settings = settings;
        _projectRoot = _policy.ResolveProjectRoot(projectRoot);
        _logger = logger;
    }

    public IReadOnlyList<string> ModifiedFiles
        => _modifiedFiles.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    internal Task<string> ReadFileForTestAsync(string path, CancellationToken cancellationToken = default)
        => ReadFileAsync(path, cancellationToken);

    internal Task WriteFileForTestAsync(string path, string content, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, content, mode: null, cancellationToken);

    protected override Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = NormalizeRelativeSessionPath(path, allowEmpty: false);
        var resolvedFile = _policy.ResolveReadableFileFromResolvedRoot(_projectRoot, relativePath);
        return Task.FromResult(File.ReadAllText(resolvedFile, Encoding.UTF8));
    }

    protected override Task WriteFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = NormalizeRelativeSessionPath(path, allowEmpty: false);
        EnsureWritesAllowed();
        _policy.EnsurePromptWithinLimit(content, nameof(content));
        var resolvedFile = _policy.ResolveWritableFileFromResolvedRoot(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedFile)!);
        File.WriteAllText(resolvedFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TrackModifiedFile(resolvedFile);
        _logger.LogInformation("Copilot agent wrote file '{RelativePath}'.", Path.GetRelativePath(_projectRoot, resolvedFile));
        return Task.CompletedTask;
    }

    protected override Task AppendFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = NormalizeRelativeSessionPath(path, allowEmpty: false);
        EnsureWritesAllowed();
        _policy.EnsurePromptWithinLimit(content, nameof(content));
        var resolvedFile = _policy.ResolveWritableFileFromResolvedRoot(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedFile)!);
        File.AppendAllText(resolvedFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TrackModifiedFile(resolvedFile);
        _logger.LogInformation("Copilot agent appended file '{RelativePath}'.", Path.GetRelativePath(_projectRoot, resolvedFile));
        return Task.CompletedTask;
    }

    protected override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = ResolveExistingOrPotentialPath(path, allowEmpty: true);
        return Task.FromResult(File.Exists(resolvedPath) || Directory.Exists(resolvedPath));
    }

    protected override Task<SessionFsStatResult> StatAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = ResolveExistingOrPotentialPath(path, allowEmpty: true);
        if (File.Exists(resolvedPath))
        {
            var info = new FileInfo(resolvedPath);
            return Task.FromResult(new SessionFsStatResult
            {
                IsFile = true,
                IsDirectory = false,
                Size = info.Length,
                Birthtime = info.CreationTimeUtc,
                Mtime = info.LastWriteTimeUtc
            });
        }

        if (Directory.Exists(resolvedPath))
        {
            var info = new DirectoryInfo(resolvedPath);
            return Task.FromResult(new SessionFsStatResult
            {
                IsFile = false,
                IsDirectory = true,
                Size = 0,
                Birthtime = info.CreationTimeUtc,
                Mtime = info.LastWriteTimeUtc
            });
        }

        throw new FileNotFoundException($"SessionFs path '{path}' was not found.", path);
    }

    protected override Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritesAllowed();
        var resolvedPath = ResolveExistingOrPotentialPath(path, allowEmpty: false);
        if (!recursive)
        {
            var parent = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                throw new DirectoryNotFoundException($"Parent directory '{parent}' does not exist.");
        }
        Directory.CreateDirectory(resolvedPath);
        return Task.CompletedTask;
    }

    protected override Task<IList<string>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = ResolveExistingOrPotentialPath(path, allowEmpty: true);
        if (!Directory.Exists(resolvedPath))
            throw new DirectoryNotFoundException($"Directory '{path}' was not found.");

        IList<string> entries = Directory.EnumerateFileSystemEntries(resolvedPath)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Where(static name => !IgnoredDirectoryNames.Contains(name!))
            .Cast<string>()
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(entries);
    }

    protected override Task<IList<SessionFsReaddirWithTypesEntry>> ReadDirectoryWithTypesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedPath = ResolveExistingOrPotentialPath(path, allowEmpty: true);
        if (!Directory.Exists(resolvedPath))
            throw new DirectoryNotFoundException($"Directory '{path}' was not found.");

        IList<SessionFsReaddirWithTypesEntry> entries = Directory.EnumerateFileSystemEntries(resolvedPath)
            .Select(entry => new { FullPath = entry, Name = Path.GetFileName(entry) })
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Where(static entry => !IgnoredDirectoryNames.Contains(entry.Name!))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => new SessionFsReaddirWithTypesEntry
            {
                Name = entry.Name!,
                Type = Directory.Exists(entry.FullPath)
                    ? SessionFsReaddirWithTypesEntryType.Directory
                    : SessionFsReaddirWithTypesEntryType.File
            })
            .ToArray();
        return Task.FromResult(entries);
    }

    protected override Task RemoveAsync(string path, bool recursive, bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritesAllowed();
        var resolvedPath = ResolveExistingOrPotentialPath(path, allowEmpty: false);
        if (File.Exists(resolvedPath))
        {
            EnsureAllowedWritableFile(resolvedPath);
            File.Delete(resolvedPath);
            TrackModifiedFile(resolvedPath);
            return Task.CompletedTask;
        }

        if (Directory.Exists(resolvedPath))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(resolvedPath).Any())
                throw new IOException($"Directory '{path}' is not empty.");
            Directory.Delete(resolvedPath, recursive);
            return Task.CompletedTask;
        }

        if (!force)
            throw new FileNotFoundException($"SessionFs path '{path}' was not found.", path);
        return Task.CompletedTask;
    }

    protected override Task RenameAsync(string src, string dest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritesAllowed();
        var sourcePath = ResolveExistingOrPotentialPath(src, allowEmpty: false);
        var destinationPath = ResolveExistingOrPotentialPath(dest, allowEmpty: false);
        if (File.Exists(sourcePath))
        {
            EnsureAllowedWritableFile(sourcePath);
            EnsureAllowedWritableFile(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath, overwrite: true);
            TrackModifiedFile(sourcePath);
            TrackModifiedFile(destinationPath);
            return Task.CompletedTask;
        }

        if (Directory.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            Directory.Move(sourcePath, destinationPath);
            return Task.CompletedTask;
        }

        throw new FileNotFoundException($"SessionFs path '{src}' was not found.", src);
    }

    private void EnsureWritesAllowed()
    {
        if (!_settings.AllowWrites)
            throw new InvalidOperationException("Copilot agent file edits are disabled by policy. Set Code:AllowWrites=true to enable code_agent_edit.");
    }

    private void EnsureAllowedWritableFile(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_projectRoot, fullPath);
        _ = _policy.ResolveWritableFileFromResolvedRoot(_projectRoot, relativePath);
    }

    private string ResolveExistingOrPotentialPath(string path, bool allowEmpty)
    {
        var relativePath = NormalizeRelativeSessionPath(path, allowEmpty);
        var fullPath = string.IsNullOrWhiteSpace(relativePath)
            ? _projectRoot
            : Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        if (!CodePolicy.IsPathWithinRoot(fullPath, _projectRoot))
            throw new InvalidOperationException($"SessionFs path '{path}' resolves outside the project root.");
        return fullPath;
    }

    private string NormalizeRelativeSessionPath(string path, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (allowEmpty)
                return string.Empty;
            throw new InvalidOperationException("SessionFs path must not be empty.");
        }

        var trimmed = path.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(trimmed))
        {
            var absolute = Path.GetFullPath(trimmed);
            if (!CodePolicy.IsPathWithinRoot(absolute, _projectRoot))
                throw new InvalidOperationException($"SessionFs path '{path}' resolves outside the project root.");
            return Path.GetRelativePath(_projectRoot, absolute);
        }

        if (HasDriveRelativePrefix(trimmed))
            throw new InvalidOperationException("SessionFs paths must be relative to the project root.");
        if (trimmed.IndexOfAny(['*', '?']) >= 0)
            throw new InvalidOperationException("SessionFs paths must not contain wildcard characters.");
        if (trimmed.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(static segment => segment == ".."))
            throw new InvalidOperationException("SessionFs paths must not contain parent traversal segments.");

        return trimmed.TrimStart(Path.DirectorySeparatorChar);
    }

    private void TrackModifiedFile(string fullPath)
    {
        if (CodePolicy.IsPathWithinRoot(fullPath, _projectRoot))
            _modifiedFiles.Add(Path.GetRelativePath(_projectRoot, fullPath));
    }

    private static bool HasDriveRelativePrefix(string path)
        => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}

