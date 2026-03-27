using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Cmd.Mcp;

public sealed class CommandPolicy
{
    private static readonly Regex PlaceholderRegex = new("{{\\s*([A-Za-z0-9_]+)\\s*}}", RegexOptions.Compiled);

    private readonly CmdServerSettings _settings;
    private readonly string _contentRootPath;
    private readonly string? _workspaceRootPath;

    public CommandPolicy(IOptions<CmdServerSettings> settings)
        : this(settings.Value, AppContext.BaseDirectory)
    {
    }

    public CommandPolicy(CmdServerSettings settings, string contentRootPath)
    {
        _settings = settings;
        _contentRootPath = Path.GetFullPath(contentRootPath);
        _workspaceRootPath = DiscoverWorkspaceRoot(_contentRootPath);
    }

    public AllowedCommandSettings GetRequiredCommand(string commandName)
    {
        var normalizedName = NormalizeRequiredValue(commandName, nameof(commandName));
        if (!_settings.AllowedCommands.TryGetValue(normalizedName, out var command))
        {
            throw new InvalidOperationException(
                $"Command '{normalizedName}' is not allowed. Use cmd_list_allowed_commands to inspect the configured allowlist.");
        }

        if (string.IsNullOrWhiteSpace(command.Script))
            throw new InvalidOperationException($"Allowed command '{normalizedName}' has no configured script.");

        return command;
    }

    public IReadOnlyList<CmdAllowedCommandInfo> ListAllowedCommands()
        => _settings.AllowedCommands
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new CmdAllowedCommandInfo(
                Name: kv.Key,
                Description: kv.Value.Description,
                Shell: kv.Value.Shell,
                WorkingDirectory: ResolveWorkingDirectory(kv.Value.WorkingDirectory),
                Parameters: kv.Value.Parameters.Keys.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();

    public CmdPolicyInfo DescribePolicy()
        => new(
            AllowedShells: [.. _settings.AllowedShells.Distinct(StringComparer.OrdinalIgnoreCase)],
            AllowedWorkingRoots: [.. ResolveAllowedWorkingRoots()],
            DefaultTimeoutMs: _settings.DefaultTimeoutMs,
            MaxTimeoutMs: _settings.MaxTimeoutMs,
            MaxOutputCharacters: _settings.MaxOutputCharacters,
            AllowedCommandCount: _settings.AllowedCommands.Count,
            Environment: DetectEnvironment());

    /// <summary>
    /// Detects the current OS, architecture, and which configured shells are actually available.
    /// </summary>
    public CmdEnvironmentInfo DetectEnvironment()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "unknown";

        var availableShells = new List<CmdShellAvailability>();
        foreach (var shellName in _settings.AllowedShells.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = ResolveShell(shellName);
                availableShells.Add(new CmdShellAvailability(shellName, Available: true, info.ExecutablePath));
            }
            catch
            {
                availableShells.Add(new CmdShellAvailability(shellName, Available: false, ResolvedPath: null));
            }
        }

        return new CmdEnvironmentInfo(
            OperatingSystem: os,
            Architecture: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            MachineName: System.Environment.MachineName,
            AvailableShells: availableShells);
    }

    public ShellLaunchInfo ResolveShell(string shellName)
    {
        var normalized = NormalizeRequiredValue(shellName, nameof(shellName)).ToLowerInvariant();
        if (!_settings.AllowedShells.Any(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Shell '{normalized}' is not allowed by policy.");

        return normalized switch
        {
            "powershell" => ResolveKnownShell(normalized, OperatingSystem.IsWindows()
                ? ["powershell.exe", "pwsh.exe"]
                : ["pwsh", "powershell"]),
            "sh" => ResolveKnownShell(normalized, ["sh", "/bin/sh"]),
            "cmd" => ResolveKnownShell(normalized, OperatingSystem.IsWindows()
                ? ["cmd.exe"]
                : throw new InvalidOperationException("Shell 'cmd' is only available on Windows.")),
            _ => throw new InvalidOperationException($"Shell '{normalized}' is not supported.")
        };
    }

    public string ResolveWorkingDirectory(string? configuredPath)
    {
        var basePath = _workspaceRootPath ?? _contentRootPath;
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? basePath
            : Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(basePath, configuredPath));

        var allowedRoots = ResolveAllowedWorkingRoots();
        if (allowedRoots.Count == 0)
            throw new InvalidOperationException("Cmd:AllowedWorkingRoots must contain at least one allowed root.");

        if (!allowedRoots.Any(root => IsPathWithinRoot(candidate, root)))
            throw new InvalidOperationException(
                $"Working directory '{candidate}' is outside the allowed roots: {string.Join(", ", allowedRoots)}.");

        if (!Directory.Exists(candidate))
            throw new InvalidOperationException($"Working directory '{candidate}' does not exist.");

        return candidate;
    }

    public int ResolveTimeoutMs(AllowedCommandSettings command, int? requestedTimeoutMs)
    {
        var effective = requestedTimeoutMs ?? command.TimeoutMs ?? _settings.DefaultTimeoutMs;
        if (effective <= 0)
            throw new InvalidOperationException("Timeout values must be greater than zero.");

        return Math.Min(effective, _settings.MaxTimeoutMs);
    }

    public int ResolveOutputLimit(AllowedCommandSettings command)
    {
        var effective = command.MaxOutputCharacters ?? _settings.MaxOutputCharacters;
        if (effective <= 0)
            throw new InvalidOperationException("Max output size must be greater than zero.");

        return effective;
    }

    public Dictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in _settings.EnvironmentAllowList.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(variable))
                continue;

            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
                environment[variable] = value;
        }

        return environment;
    }

    public string RenderScript(AllowedCommandSettings command, JsonObject? parameters)
    {
        parameters ??= new JsonObject();

        foreach (var supplied in parameters.Select(kv => kv.Key))
        {
            if (!command.Parameters.ContainsKey(supplied))
                throw new InvalidOperationException($"Parameter '{supplied}' is not declared for this command.");
        }

        foreach (var parameter in command.Parameters)
        {
            var value = parameters[parameter.Key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (parameter.Value.Required)
                    throw new InvalidOperationException($"Missing required parameter '{parameter.Key}'.");
                continue;
            }

            if (value.Length > parameter.Value.MaxLength)
                throw new InvalidOperationException($"Parameter '{parameter.Key}' exceeds max length {parameter.Value.MaxLength}.");

            if (!Regex.IsMatch(value, parameter.Value.Pattern))
                throw new InvalidOperationException($"Parameter '{parameter.Key}' does not match the required pattern.");
        }

        var rendered = PlaceholderRegex.Replace(command.Script, match =>
        {
            var parameterName = match.Groups[1].Value;
            var parameterValue = parameters[parameterName]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(parameterValue))
                throw new InvalidOperationException($"Missing value for placeholder '{parameterName}'.");

            return EscapeForShell(command.Shell, parameterValue);
        });

        if (PlaceholderRegex.IsMatch(rendered))
            throw new InvalidOperationException("Command script still contains unresolved placeholders.");

        return rendered;
    }

    internal static string EscapeForShell(string shellName, string value)
    {
        var normalized = NormalizeRequiredValue(shellName, nameof(shellName)).ToLowerInvariant();
        return normalized switch
        {
            "powershell" => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'",
            "sh" => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'",
            "cmd" => "\"" + CmdEscapeSpecialChars(value) + "\"",
            _ => throw new InvalidOperationException($"Shell '{normalized}' is not supported for escaping.")
        };
    }

    /// <summary>
    /// Escapes cmd.exe special characters with the ^ prefix.
    /// </summary>
    private static string CmdEscapeSpecialChars(string value)
    {
        ReadOnlySpan<char> special = ['&', '|', '<', '>', '^', '%', '(', ')'];
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (special.Contains(ch))
                sb.Append('^');
            sb.Append(ch);
        }
        return sb.ToString();
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
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private List<string> ResolveAllowedWorkingRoots()
    {
        var basePath = _workspaceRootPath ?? _contentRootPath;
        return _settings.AllowedWorkingRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(basePath, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ShellLaunchInfo ResolveKnownShell(string logicalName, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryResolveExecutable(candidate, out var resolvedPath))
            {
                return logicalName switch
                {
                    "powershell" => new ShellLaunchInfo(logicalName, resolvedPath, script => $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
                    "sh" => new ShellLaunchInfo(logicalName, resolvedPath, script => $"-c \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
                    "cmd" => new ShellLaunchInfo(logicalName, resolvedPath, script => $"/C \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
                    _ => throw new InvalidOperationException($"Shell '{logicalName}' is not supported.")
                };
            }
        }

        throw new InvalidOperationException($"No executable was found for allowed shell '{logicalName}'.");
    }

    internal static bool TryResolveExecutable(string candidate, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (Path.IsPathRooted(candidate) && File.Exists(candidate))
        {
            resolvedPath = Path.GetFullPath(candidate);
            return true;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return false;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [".exe", ".cmd", ".bat"])
            : [string.Empty];

        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var withExtension = OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(candidate))
                    ? candidate + extension
                    : candidate;
                var fullPath = Path.Combine(dir, withExtension);
                if (File.Exists(fullPath))
                {
                    resolvedPath = fullPath;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeRequiredValue(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{parameterName} must not be empty.");

        return normalized;
    }
}

public sealed record CmdPolicyInfo(
    IReadOnlyList<string> AllowedShells,
    IReadOnlyList<string> AllowedWorkingRoots,
    int DefaultTimeoutMs,
    int MaxTimeoutMs,
    int MaxOutputCharacters,
    int AllowedCommandCount,
    CmdEnvironmentInfo Environment);

public sealed record CmdEnvironmentInfo(
    string OperatingSystem,
    string Architecture,
    string MachineName,
    IReadOnlyList<CmdShellAvailability> AvailableShells);

public sealed record CmdShellAvailability(
    string Name,
    bool Available,
    string? ResolvedPath);

public sealed record CmdAllowedCommandInfo(
    string Name,
    string? Description,
    string Shell,
    string WorkingDirectory,
    IReadOnlyList<string> Parameters);

public sealed record ShellLaunchInfo(
    string LogicalName,
    string ExecutablePath,
    Func<string, string> BuildArguments);



