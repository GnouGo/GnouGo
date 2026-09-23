using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace GnOuGo.GithubCopilot.Core;

internal sealed class GitHubCopilotSessionFsAdapter(ICopilotSessionFileSystem project, CopilotTransientSessionState state) : SessionFsProvider
{
    private static bool IsState(string path, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return CopilotTransientSessionState.Contains(path); }
    protected override Task<string> ReadFileAsync(string path, CancellationToken ct)
        => IsState(path, ct) ? Task.FromResult(state.Read(path)) : project.ReadFileAsync(path, ct);
    protected override Task WriteFileAsync(string path, string content, int? mode, CancellationToken ct)
    { if (!IsState(path, ct)) return project.WriteFileAsync(path, content, mode, ct); state.Write(path, content, false); return Task.CompletedTask; }
    protected override Task AppendFileAsync(string path, string content, int? mode, CancellationToken ct)
    { if (!IsState(path, ct)) return project.AppendFileAsync(path, content, mode, ct); state.Write(path, content, true); return Task.CompletedTask; }
    protected override Task<bool> ExistsAsync(string path, CancellationToken ct)
        => IsState(path, ct) ? Task.FromResult(state.Exists(path)) : project.ExistsAsync(path, ct);
    protected override async Task<SessionFsStatResult> StatAsync(string path, CancellationToken ct)
    {
        var stat = IsState(path, ct) ? state.Stat(path) : await project.StatAsync(path, ct);
        return new() { IsFile = stat.IsFile, IsDirectory = stat.IsDirectory, Size = stat.Size, Birthtime = stat.Birthtime, Mtime = stat.Mtime };
    }
    protected override Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken ct)
    { if (!IsState(path, ct)) return project.MakeDirectoryAsync(path, recursive, mode, ct); state.Directory(path, recursive); return Task.CompletedTask; }
    private Task<IReadOnlyList<CopilotDirectoryEntry>> ListAsync(string path, CancellationToken ct)
        => IsState(path, ct) ? Task.FromResult(state.List(path)) : project.ReadDirectoryAsync(path, ct);
    protected override async Task<IList<string>> ReadDirectoryAsync(string path, CancellationToken ct)
        => (await ListAsync(path, ct)).Select(e => e.Name).ToArray();
    protected override async Task<IList<SessionFsReaddirWithTypesEntry>> ReadDirectoryWithTypesAsync(string path, CancellationToken ct)
        => (await ListAsync(path, ct)).Select(e => new SessionFsReaddirWithTypesEntry { Name = e.Name, Type = e.IsDirectory ? SessionFsReaddirWithTypesEntryType.Directory : SessionFsReaddirWithTypesEntryType.File }).ToArray();
    protected override Task RemoveAsync(string path, bool recursive, bool force, CancellationToken ct)
    { if (!IsState(path, ct)) return project.RemoveAsync(path, recursive, force, ct); state.Remove(path, recursive, force); return Task.CompletedTask; }
    protected override Task RenameAsync(string src, string dest, CancellationToken ct)
    {
        var sourceState = IsState(src, ct); var destinationState = IsState(dest, ct);
        if (sourceState != destinationState) throw new UnauthorizedAccessException("Cannot move files across the session-state boundary.");
        if (!sourceState) return project.RenameAsync(src, dest, ct);
        state.Rename(src, dest); return Task.CompletedTask;
    }
}
