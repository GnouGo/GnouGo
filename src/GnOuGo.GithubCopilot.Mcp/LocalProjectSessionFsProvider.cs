using System.Text;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class LocalProjectSessionFsFactory(CodePolicy policy, IOptions<CodeServerSettings> settings, ILoggerFactory logs) : ICopilotSessionFileSystemFactory
{
    public ICopilotSessionFileSystem Create(CopilotSessionCreateRequest request)
        => new LocalProjectSessionFsProvider(policy, settings.Value, request.Configuration.WorkingDirectory, logs.CreateLogger<LocalProjectSessionFsProvider>());
}

internal sealed class LocalProjectSessionFsProvider : ICopilotSessionFileSystem
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    { ".git", GnOuGo.Workspace.GnOuGoWorkspace.WorkspaceDataSubfolder, "bin", "obj", "node_modules", ".vs", ".idea", ".vscode" };
    private readonly CodePolicy _policy;
    private readonly CodeServerSettings _settings;
    private readonly string _projectRoot;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _modifiedFiles = new(StringComparer.Ordinal);
    private bool _disposed;

    public LocalProjectSessionFsProvider(CodePolicy policy, CodeServerSettings settings, string projectRoot, ILogger logger)
    {
        _policy = policy; _settings = settings; _logger = logger;
        _projectRoot = policy.ResolveProjectRoot(projectRoot);
        _policy.EnsureNoSymbolicLinks(_projectRoot);
    }

    public IReadOnlyList<string> ModifiedFiles { get { lock (_gate) return _modifiedFiles.Order(StringComparer.Ordinal).ToArray(); } }
    public void ValidateRead(string path)
    {
        var full = Resolve(path, true);
        if (Directory.Exists(full)) return;
        _ = _policy.ResolveReadableFileFromResolvedRoot(_projectRoot, Path.GetRelativePath(_projectRoot, full));
    }
    public void ValidateWrite(string path, string? content = null)
    {
        var full = Resolve(path, false);
        EnsureWritesAllowed();
        if (Directory.Exists(full)) ValidateTree(full);
        else _ = _policy.ResolveWritableFileFromResolvedRoot(_projectRoot, Path.GetRelativePath(_projectRoot, full));
        if (File.Exists(full)) ValidateRead(full);
        if (content is not null) CheckContent(content);
    }
    public void ValidateDirectoryWrite(string path)
    {
        EnsureWritesAllowed();
        _ = Resolve(path, false);
    }
    public void ValidateRename(string source, string destination)
    {
        ValidateWrite(source);
        var src = Resolve(source, false);
        if (!Directory.Exists(src)) { ValidateWrite(destination); return; }
        ValidateDirectoryWrite(destination);
        var dest = Resolve(destination, false);
        foreach (var file in ValidateTree(src)) ValidateWrite(Path.Combine(dest, Path.GetRelativePath(src, file)));
    }
    internal Task<string> ReadFileForTestAsync(string path, CancellationToken cancellationToken = default) => ReadFileAsync(path, cancellationToken);
    internal Task WriteFileForTestAsync(string path, string content, CancellationToken cancellationToken = default) => WriteFileAsync(path, content, null, cancellationToken);

    public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { ValidateRead(path); return Task.FromResult(File.ReadAllText(Resolve(path, false), Encoding.UTF8)); }
    }
    public Task WriteFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
        => WriteAsync(path, content, false, cancellationToken);
    public Task AppendFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
        => WriteAsync(path, content, true, cancellationToken);
    private Task WriteAsync(string path, string content, bool append, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ValidateWrite(path, content);
            var full = Resolve(path, false);
            if (append && File.Exists(full))
            {
                ValidateRead(path);
                content = File.ReadAllText(full, Encoding.UTF8) + content;
                CheckContent(content);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            _policy.EnsureNoSymbolicLinks(full);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            Track(full);
            _logger.LogInformation("Copilot wrote project file {RelativePath}.", Path.GetRelativePath(_projectRoot, full));
        }
        return Task.CompletedTask;
    }
    public Task<bool> ExistsAsync(string path, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (_gate) { var full = Resolve(path, true); ValidateMetadata(full); return Task.FromResult(File.Exists(full) || Directory.Exists(full)); } }
    public Task<CopilotFileStat> StatAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var full = Resolve(path, true); ValidateMetadata(full);
            if (File.Exists(full)) { var f = new FileInfo(full); return Task.FromResult(new CopilotFileStat(true, false, f.Length, f.CreationTimeUtc, f.LastWriteTimeUtc)); }
            if (Directory.Exists(full)) { var d = new DirectoryInfo(full); return Task.FromResult(new CopilotFileStat(false, true, 0, d.CreationTimeUtc, d.LastWriteTimeUtc)); }
            throw new FileNotFoundException("Session filesystem path not found.", path);
        }
    }
    public Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureWritesAllowed(); var full = Resolve(path, false);
            if (!recursive && !Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException();
            Directory.CreateDirectory(full);
        }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<CopilotDirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var full = Resolve(path, true);
            var entries = new List<CopilotDirectoryEntry>();
            foreach (var item in Directory.EnumerateFileSystemEntries(full))
            {
                if (IgnoredDirectoryNames.Contains(Path.GetFileName(item))) continue;
                try { Resolve(item, false); ValidateMetadata(item); }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { continue; }
                entries.Add(new(Path.GetFileName(item), Directory.Exists(item)));
            }
            return Task.FromResult<IReadOnlyList<CopilotDirectoryEntry>>(entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray());
        }
    }
    public Task RemoveAsync(string path, bool recursive, bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ValidateWrite(path); var full = Resolve(path, false);
            if (File.Exists(full)) { File.Delete(full); Track(full); }
            else if (Directory.Exists(full))
            {
                var files = ValidateTree(full);
                Directory.Delete(full, recursive);
                foreach (var file in files) Track(file);
            }
            else if (!force) throw new FileNotFoundException("Session filesystem path not found.", path);
        }
        return Task.CompletedTask;
    }
    public Task RenameAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureWritesAllowed(); var src = Resolve(source, false); var dest = Resolve(destination, false);
            if (File.Exists(src))
            {
                ValidateWrite(src); ValidateWrite(dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(src, dest, true); Track(src); Track(dest);
            }
            else if (Directory.Exists(src))
            {
                var files = ValidateTree(src);
                foreach (var file in files) ValidateWrite(Path.Combine(dest, Path.GetRelativePath(src, file)));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Directory.Move(src, dest);
                foreach (var file in files) { Track(file); Track(Path.Combine(dest, Path.GetRelativePath(src, file))); }
            }
            else throw new FileNotFoundException("Session filesystem path not found.", source);
        }
        return Task.CompletedTask;
    }
    private List<string> ValidateTree(string directory)
    {
        Resolve(directory, false);
        var files = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            Resolve(entry, false);
            if (Directory.Exists(entry)) files.AddRange(ValidateTree(entry));
            else { ValidateWrite(entry); files.Add(entry); }
        }
        return files;
    }
    private void ValidateMetadata(string full)
    {
        if (!Directory.Exists(full)) _policy.EnsureAllowedFile(full);
        if (File.Exists(full)) _ = _policy.ResolveReadableFileFromResolvedRoot(_projectRoot, Path.GetRelativePath(_projectRoot, full));
    }
    private string Resolve(string path, bool allowRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path)) { if (allowRoot) return _projectRoot; throw new InvalidOperationException("Session filesystem path must not be empty."); }
        var normalized = path.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (normalized.IndexOfAny(['*', '?']) >= 0 || normalized.Split(Path.DirectorySeparatorChar).Any(s => s == ".."))
            throw new InvalidOperationException("Session filesystem paths must not contain parent traversal or wildcard characters.");
        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && !Path.IsPathFullyQualified(normalized))
            throw new InvalidOperationException("Drive-relative paths are not supported.");
        var full = Path.GetFullPath(normalized, _projectRoot);
        var relative = Path.GetRelativePath(_projectRoot, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Session filesystem path is outside the project root.");
        if (!allowRoot && relative == ".") throw new UnauthorizedAccessException("The project root cannot be mutated.");
        _policy.EnsureOutsideReservedWorkspace(full);
        if (relative.Split(Path.DirectorySeparatorChar).Any(IgnoredDirectoryNames.Contains))
            throw new UnauthorizedAccessException("The path is in a protected project directory.");
        _policy.EnsureNoSymbolicLinks(full);
        return full;
    }
    private void CheckContent(string content)
    {
        _policy.EnsurePromptWithinLimit(content, nameof(content));
        if (Encoding.UTF8.GetByteCount(content) > _settings.MaxFileSizeBytes) throw new InvalidOperationException("File content exceeds the configured size limit.");
    }
    private void EnsureWritesAllowed() { if (!_settings.AllowWrites) throw new InvalidOperationException("Copilot file edits are disabled by policy. Set Code:AllowWrites=true to enable writes."); }
    private void Track(string path) => _modifiedFiles.Add(Path.GetRelativePath(_projectRoot, path));
    public ValueTask DisposeAsync() { lock (_gate) _disposed = true; return ValueTask.CompletedTask; }
}
