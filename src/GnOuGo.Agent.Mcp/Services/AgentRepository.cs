using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Workspace;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class AgentRepository : IAgentRepository
{
    private readonly string _agentsDirectory;
    private readonly string _workspaceRoot;

    public AgentRepository(string agentsDirectory, string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsDirectory);
        _agentsDirectory = Path.GetFullPath(agentsDirectory);
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? InferWorkspaceRoot(_agentsDirectory)
            : Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(_agentsDirectory);
        Directory.CreateDirectory(_workspaceRoot);
    }

    public async Task<AgentDefinition> AddAgentAsync(string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var normalizedName = NormalizeName(name);
        EnsureSafeAgentName(normalizedName);
        await EnsureNameAvailableAsync(normalizedName, excludedAgentId: null, ct);

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            Workflow = workflow,
            OriginalPrompt = originalPrompt,
            CreatedAt = now,
            UpdatedAt = now
        };

        await SaveAgentFileAsync(agent, ct);
        return agent;
    }

    public async Task<AgentDefinition> AddAgentBundleAsync(
        string name,
        string workflow,
        IReadOnlyDictionary<string, string>? workflows,
        string? originalPrompt = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var bundleFiles = NormalizeBundleFiles(workflows, workflow);
        var agent = await AddAgentAsync(name, workflow, originalPrompt, ct);
        await SaveBundleFilesAsync(bundleFiles, ct);
        return agent;
    }

    public async Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, string? originalPrompt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var normalizedName = NormalizeName(name);
        EnsureSafeAgentName(normalizedName);
        await EnsureNameAvailableAsync(normalizedName, id, ct);

        var existing = await GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        var previousPath = GetAgentPath(existing.Name);

        existing.Name = normalizedName;
        existing.Workflow = workflow;
        existing.OriginalPrompt = originalPrompt;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveAgentFileAsync(existing, ct);
        var newPath = GetAgentPath(existing.Name);
        if (!string.Equals(previousPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(previousPath))
            File.Delete(previousPath);

        return existing;
    }

    public async Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
    {
        var agents = new List<AgentDefinition>();
        foreach (var file in Directory.EnumerateFiles(_agentsDirectory, "*.yaml", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            agents.Add(await LoadAgentFileAsync(file, ct));
        }

        return agents.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalizedName = NormalizeName(name);
        foreach (var agent in await ListAgentsAsync(ct))
        {
            if (string.Equals(agent.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                return agent;
        }

        return null;
    }

    public async Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        File.Delete(GetAgentPath(agent.Name));
    }

    internal static string SerializeAgentToYaml(AgentDefinition agent)
    {
        var snapshot = new AgentSnapshot
        {
            Id = agent.Id.ToString(),
            Name = agent.Name,
            Workflow = agent.Workflow,
            OriginalPrompt = agent.OriginalPrompt ?? "",
            CreatedAt = agent.CreatedAt.ToString("o"),
            UpdatedAt = agent.UpdatedAt.ToString("o")
        };

        return AgentMcpYamlContext.Serialize(snapshot);
    }

    private async Task EnsureNameAvailableAsync(string normalizedName, Guid? excludedAgentId, CancellationToken ct)
    {
        var existing = await GetByNameAsync(normalizedName, ct);

        if (existing is not null && existing.Id != excludedAgentId)
            throw new DuplicateAgentNameException(normalizedName);
    }

    private async Task<AgentDefinition?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        foreach (var agent in await ListAgentsAsync(ct))
            if (agent.Id == id)
                return agent;

        return null;
    }

    private async Task SaveAgentFileAsync(AgentDefinition agent, CancellationToken ct)
    {
        Directory.CreateDirectory(_agentsDirectory);
        var targetPath = GetAgentPath(agent.Name);
        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");

        await File.WriteAllTextAsync(tempPath, SerializeAgentToYaml(agent), ct);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private async Task<AgentDefinition> LoadAgentFileAsync(string path, CancellationToken ct)
    {
        var yaml = await File.ReadAllTextAsync(path, ct);
        return DeserializeAgentYaml(yaml, Path.GetFileNameWithoutExtension(path));
    }

    private string GetAgentPath(string name)
        => Path.Combine(_agentsDirectory, NormalizeName(name) + ".yaml");

    private static string InferWorkspaceRoot(string agentsDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(agentsDirectory));
        if (string.Equals(directory.Name, GnOuGoWorkspace.WorkspaceDataSubfolder, StringComparison.OrdinalIgnoreCase)
            && directory.Parent is not null)
        {
            return directory.Parent.FullName;
        }

        return directory.Parent?.FullName ?? directory.FullName;
    }

    private static Dictionary<string, string> NormalizeBundleFiles(IReadOnlyDictionary<string, string>? workflows, string mainWorkflow)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (workflows is null || workflows.Count == 0)
            return files;

        foreach (var (path, content) in workflows)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException($"Bundled workflow '{path}' cannot be empty.", nameof(workflows));

            var normalizedPath = NormalizeBundleRelativePath(path);
            if (!normalizedPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !normalizedPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Bundled workflow path '{path}' must be a YAML file.", nameof(workflows));
            }

            files[normalizedPath] = content;
        }

        var mainPath = SelectMainBundlePath(files.Keys);
        if (mainPath != null)
            files[mainPath] = mainWorkflow;

        return files;
    }

    private static string? SelectMainBundlePath(IEnumerable<string> paths)
    {
        return paths
            .Where(static path => path.EndsWith("/workflow.yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path.Count(static ch => ch == '/'))
            .ThenBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string NormalizeBundleRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Bundled workflow path cannot be empty.", nameof(path));

        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        if (Path.IsPathRooted(normalized)
            || normalized is "." or ".."
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal)
            || normalized.EndsWith("/..", StringComparison.Ordinal)
            || normalized.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Bundled workflow path '{path}' must be a safe relative path.", nameof(path));
        }

        return normalized;
    }

    private async Task SaveBundleFilesAsync(IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                _workspaceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!GnOuGoWorkspace.IsPathWithinRoot(fullPath, _workspaceRoot))
                throw new ArgumentException($"Bundled workflow path '{relativePath}' escapes the workspace root.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(tempPath, content, ct);
            File.Move(tempPath, fullPath, overwrite: true);
        }
    }

    private static AgentDefinition DeserializeAgentYaml(string yaml, string fallbackName)
    {
        var values = ParseTopLevelYaml(yaml);
        var id = Guid.TryParse(values.GetValueOrDefault("id"), out var parsedId) ? parsedId : Guid.NewGuid();
        var createdAt = DateTimeOffset.TryParse(values.GetValueOrDefault("createdAt"), out var parsedCreatedAt)
            ? parsedCreatedAt
            : DateTimeOffset.UtcNow;
        var updatedAt = DateTimeOffset.TryParse(values.GetValueOrDefault("updatedAt"), out var parsedUpdatedAt)
            ? parsedUpdatedAt
            : createdAt;

        return new AgentDefinition
        {
            Id = id,
            Name = values.GetValueOrDefault("name") ?? fallbackName,
            Workflow = values.GetValueOrDefault("workflow") ?? "",
            OriginalPrompt = values.GetValueOrDefault("originalPrompt"),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    private static Dictionary<string, string> ParseTopLevelYaml(string yaml)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]))
                continue;

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            var key = line[..separator];
            var value = line[(separator + 1)..].TrimStart();
            if (value is "|-" or "|")
            {
                var blockLines = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].StartsWith("  ", StringComparison.Ordinal) || string.IsNullOrEmpty(lines[i + 1])))
                {
                    i++;
                    blockLines.Add(lines[i].StartsWith("  ", StringComparison.Ordinal) ? lines[i][2..] : lines[i]);
                }

                values[key] = string.Join('\n', blockLines).TrimEnd('\n');
            }
            else
            {
                values[key] = Unquote(value);
            }
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return value;
    }

    private static void EnsureSafeAgentName(string name)
    {
        if (name is "." or "..")
            throw new ArgumentException("Agent name cannot be '.' or '..'.", nameof(name));

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent name contains characters that are not valid in a file name.", nameof(name));
        }
    }

    private static string NormalizeName(string name) => name.Trim();
}
