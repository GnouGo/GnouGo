namespace GnOuGo.GithubCopilot.Core;

/// <summary>Host-owned project policy. Approval never overrides this boundary.</summary>
public interface ICopilotFileAccessPolicy
{
    void ValidateRead(string path);
    void ValidateWrite(string path, string? content = null);
}

public interface ICopilotSessionFileSystemFactory
{
    ICopilotSessionFileSystem Create(CopilotSessionCreateRequest request);
}

/// <summary>A session-owned filesystem. Implementations must enforce policy at every operation.</summary>
public interface ICopilotSessionFileSystem : ICopilotFileAccessPolicy, IAsyncDisposable
{
    IReadOnlyList<string> ModifiedFiles { get; }
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken);
    Task WriteFileAsync(string path, string content, int? mode, CancellationToken cancellationToken);
    Task AppendFileAsync(string path, string content, int? mode, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);
    Task<CopilotFileStat> StatAsync(string path, CancellationToken cancellationToken);
    Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotDirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken);
    Task RemoveAsync(string path, bool recursive, bool force, CancellationToken cancellationToken);
    Task RenameAsync(string source, string destination, CancellationToken cancellationToken);
}

public sealed record CopilotFileStat(bool IsFile, bool IsDirectory, long Size, DateTime Birthtime, DateTime Mtime);
public sealed record CopilotDirectoryEntry(string Name, bool IsDirectory);

/// <summary>Session-private state is memory-only and never passes through the project policy.</summary>
internal sealed class CopilotTransientSessionState
{
    internal const string Root = "/__gnougo_session__";
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { Root };

    internal static bool Contains(string path) => path.Replace('\\', '/').TrimEnd('/') is var normalized
        && (normalized == Root || normalized.StartsWith(Root + "/", StringComparison.Ordinal));

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (!Contains(normalized) || normalized.Split('/').Any(p => p is ".." or "."))
            throw new UnauthorizedAccessException("Invalid session-state path.");
        return normalized;
    }

    internal string Read(string path) { lock (_gate) return _files.TryGetValue(Normalize(path), out var value) ? value : throw new FileNotFoundException("Session file not found."); }
    internal void Write(string path, string content, bool append)
    {
        lock (_gate)
        {
            path = Normalize(path);
            Directory(Path.GetDirectoryName(path)!.Replace('\\', '/'), true);
            _files[path] = append && _files.TryGetValue(path, out var previous) ? previous + content : content;
        }
    }
    internal bool Exists(string path) { lock (_gate) return _files.ContainsKey(Normalize(path)) || _directories.Contains(Normalize(path)); }
    internal CopilotFileStat Stat(string path)
    {
        lock (_gate)
        {
            path = Normalize(path);
            if (!Exists(path)) throw new FileNotFoundException("Session path not found.");
            var file = _files.TryGetValue(path, out var text);
            return new(file, !file, file ? System.Text.Encoding.UTF8.GetByteCount(text!) : 0, DateTime.UnixEpoch, DateTime.UnixEpoch);
        }
    }
    internal void Directory(string path, bool recursive)
    {
        lock (_gate)
        {
            path = Normalize(path);
            if (_directories.Contains(path)) return;
            var parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
            if (recursive) Directory(parent, true);
            else if (!_directories.Contains(parent)) throw new DirectoryNotFoundException();
            _directories.Add(path);
        }
    }
    internal IReadOnlyList<CopilotDirectoryEntry> List(string path)
    {
        lock (_gate)
        {
            path = Normalize(path);
            if (!_directories.Contains(path)) throw new DirectoryNotFoundException();
            return _directories.Concat(_files.Keys).Where(p => p != path && Path.GetDirectoryName(p)?.Replace('\\', '/') == path)
                .Select(p => new CopilotDirectoryEntry(Path.GetFileName(p), _directories.Contains(p))).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        }
    }
    internal void Remove(string path, bool recursive, bool force)
    {
        lock (_gate)
        {
            path = Normalize(path);
            if (_files.Remove(path)) return;
            if (!_directories.Contains(path)) { if (!force) throw new FileNotFoundException(); return; }
            if (!recursive && List(path).Count != 0) throw new IOException("Directory is not empty.");
            foreach (var key in _files.Keys.Where(p => p.StartsWith(path + "/", StringComparison.Ordinal)).ToArray()) _files.Remove(key);
            _directories.RemoveWhere(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal));
        }
    }
    internal void Rename(string source, string destination)
    {
        lock (_gate)
        {
            source = Normalize(source); destination = Normalize(destination);
            if (_files.TryGetValue(source, out var text)) { Write(destination, text, false); _files.Remove(source); return; }
            if (!_directories.Contains(source)) throw new DirectoryNotFoundException();
            if (destination.StartsWith(source + "/", StringComparison.Ordinal)) throw new IOException("Cannot move a directory into itself.");
            Directory(destination, true);
            foreach (var dir in _directories.Where(p => p.StartsWith(source + "/", StringComparison.Ordinal)).ToArray()) _directories.Add(destination + dir[source.Length..]);
            foreach (var file in _files.Where(p => p.Key.StartsWith(source + "/", StringComparison.Ordinal)).ToArray()) _files[destination + file.Key[source.Length..]] = file.Value;
            Remove(source, true, false);
        }
    }
    internal void Clear() { lock (_gate) { _files.Clear(); _directories.Clear(); } }
}
