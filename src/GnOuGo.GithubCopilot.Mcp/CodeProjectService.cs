using System.Text;
using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

public sealed class CodeProjectService
{
    private static readonly string[] IgnoredDirectoryNames = [".git", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode"];

    private readonly CodePolicy _policy;
    private readonly CodeServerSettings _settings;

    public CodeProjectService(CodePolicy policy, IOptions<CodeServerSettings> settings)
    {
        _policy = policy;
        _settings = settings.Value;
    }

    public CodePolicyInfo GetPolicy() => _policy.DescribePolicy();

    public CodeProjectSummary GetSummary(string? projectRoot)
    {
        var root = _policy.ResolveProjectRoot(projectRoot);
        var solutionFiles = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(static n => n != null)
            .Cast<string>()
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var projectFiles = EnumerateAllowedFiles(root, "*.csproj")
            .Select(path => GnOuGoWorkspace.NormalizePortablePath(Path.GetRelativePath(root, path)))
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var topDirs = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(static n => n != null)
            .Cast<string>()
            .Where(static n => !IgnoredDirectoryNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var codeFiles = EnumerateAllowedFiles(root).ToArray();
        return new CodeProjectSummary(
            RootPath: root,
            SolutionFiles: solutionFiles,
            ProjectFiles: projectFiles,
            TopLevelDirectories: topDirs,
            CodeFileCount: codeFiles.Length,
            ApproximateBytes: codeFiles.Sum(static path => new FileInfo(path).Length),
            RootPathRelative: ToWorkspaceRelativePath(root));
    }

    public CodeFileContent ReadFile(string projectRoot, string relativePath)
    {
        var root = _policy.ResolveProjectRoot(projectRoot);
        var file = _policy.ResolveReadableFile(root, relativePath);
        var normalizedRelativePath = GnOuGoWorkspace.NormalizePortablePath(Path.GetRelativePath(root, file));
        return new CodeFileContent(
            Path: normalizedRelativePath,
            FullPath: file,
            Content: File.ReadAllText(file, Encoding.UTF8),
            LengthBytes: new FileInfo(file).Length,
            RelativePath: normalizedRelativePath);
    }

    public CodeSearchResults Search(string projectRoot, string query, string? glob = null, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("query must not be empty.");
        if (query.Length > 500)
            throw new InvalidOperationException("query is too long.");

        var root = _policy.ResolveProjectRoot(projectRoot);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var pattern = string.IsNullOrWhiteSpace(glob) ? "*" : glob.Trim();
        var results = new List<CodeSearchResult>();
        var limit = Math.Max(1, _settings.MaxSearchResults);

        foreach (var file in EnumerateAllowedFiles(root, pattern))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                lineNumber++;
                if (line.Contains(query, comparison))
                {
                    results.Add(new CodeSearchResult(GnOuGoWorkspace.NormalizePortablePath(Path.GetRelativePath(root, file)), lineNumber, TrimLine(line)));
                    if (results.Count >= limit)
                        return new CodeSearchResults(results, Truncated: true);
                }
            }
        }

        return new CodeSearchResults(results, Truncated: false);
    }

    public CodeWriteResult WriteFile(string projectRoot, string relativePath, string content)
    {
        _policy.EnsurePromptWithinLimit(content, nameof(content));
        var root = _policy.ResolveProjectRoot(projectRoot);
        var file = _policy.ResolveWritableFile(root, relativePath);
        var directory = Path.GetDirectoryName(file)!;
        var createdDirectory = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var normalizedRelativePath = GnOuGoWorkspace.NormalizePortablePath(Path.GetRelativePath(root, file));
        return new CodeWriteResult(normalizedRelativePath, file, Encoding.UTF8.GetByteCount(content), createdDirectory, normalizedRelativePath);
    }

    public IReadOnlyList<CodeFileContent> ReadContextFiles(string projectRoot, IEnumerable<string> relativePaths, int maxFiles = 8)
    {
        var files = new List<CodeFileContent>();
        foreach (var relativePath in relativePaths.Where(static p => !string.IsNullOrWhiteSpace(p)).Take(Math.Max(1, maxFiles)))
            files.Add(ReadFile(projectRoot, relativePath));
        return files;
    }

    private IEnumerable<string> EnumerateAllowedFiles(string root, string pattern = "*")
    {
        if (pattern.Contains("..", StringComparison.Ordinal) || pattern.IndexOfAny(['/', '\\']) >= 0 && pattern.Contains('*'))
            pattern = "*";

        var allowedExtensions = _settings.AllowedExtensions
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Select(static e => e.StartsWith('.') ? e : "." + e)
            .ToArray();

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var subdir in SafeEnumerateDirectories(dir))
            {
                var name = Path.GetFileName(subdir);
                if (!IgnoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    stack.Push(subdir);
            }

            foreach (var file in SafeEnumerateFiles(dir, pattern))
            {
                if (allowedExtensions.Length == 0 || allowedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    var length = new FileInfo(file).Length;
                    if (length <= _settings.MaxFileSizeBytes)
                        yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToArray(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern).ToArray(); }
        catch { return []; }
    }

    private static string TrimLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

    private string? ToWorkspaceRelativePath(string path)
        => GnOuGoWorkspace.ToWorkspaceRelativePath(path, _policy.DefaultWorkingDirectory);
}
