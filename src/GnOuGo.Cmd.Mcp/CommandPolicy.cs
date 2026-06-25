using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Cmd.Mcp;

public sealed class CommandPolicy
{
    private static readonly Regex PlaceholderRegex = new("{{\\s*([A-Za-z0-9_]+)\\s*}}", RegexOptions.Compiled);

    private readonly CmdServerSettings _settings;
    private readonly string _contentRootPath;
    private readonly string? _workspaceRootPath;
    private readonly string _defaultWorkingDirectory;

    public CommandPolicy(IOptions<CmdServerSettings> settings)
        : this(settings.Value, AppContext.BaseDirectory)
    {
    }

    public CommandPolicy(CmdServerSettings settings, string contentRootPath)
    {
        _settings = settings;
        _contentRootPath = Path.GetFullPath(contentRootPath);
        _workspaceRootPath = GnOuGoWorkspace.DiscoverWorkspaceRoot(_contentRootPath);
        _defaultWorkingDirectory = ResolveDefaultWorkingDirectorySafe();
    }

    public AllowedCommandSettings GetRequiredCommand(string commandName)
    {
        var normalizedName = NormalizeRequiredValue(commandName, nameof(commandName));
        if (!_settings.AllowedCommands.TryGetValue(normalizedName, out var command))
        {
            throw new InvalidOperationException(
                $"Command '{normalizedName}' is not allowed. Use cmd_list_allowed_commands to inspect the configured allowlist.");
        }

        command = ApplyOsOverride(command);

        if (string.IsNullOrWhiteSpace(command.Script))
            throw new InvalidOperationException($"Allowed command '{normalizedName}' has no configured script.");

        return command;
    }

    /// <summary>
    /// Returns the effective command settings after applying any OS-specific override.
    /// Shell and Script are replaced when the override provides them; Parameters are merged
    /// (OS-specific entries take precedence over base entries).
    /// </summary>
    private static AllowedCommandSettings ApplyOsOverride(AllowedCommandSettings command)
    {
        if (command.OsOverrides.Count == 0)
            return command;

        var osKey = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : null;

        if (osKey is null || !command.OsOverrides.TryGetValue(osKey, out var ovr))
            return command;

        // Build merged parameters: start with base, then overlay OS-specific entries.
        var mergedParams = new Dictionary<string, CommandParameterSettings>(command.Parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ovr.Parameters)
            mergedParams[kv.Key] = kv.Value;

        return new AllowedCommandSettings
        {
            Description = command.Description,
            Shell = !string.IsNullOrWhiteSpace(ovr.Shell) ? ovr.Shell! : command.Shell,
            Script = !string.IsNullOrWhiteSpace(ovr.Script) ? ovr.Script! : command.Script,
            WorkingDirectory = command.WorkingDirectory,
            TimeoutMs = command.TimeoutMs,
            MaxOutputCharacters = command.MaxOutputCharacters,
            Parameters = mergedParams,
            OsOverrides = command.OsOverrides   // keep for introspection if needed
        };
    }

    public IReadOnlyList<CmdAllowedCommandInfo> ListAllowedCommands()
        => _settings.AllowedCommands
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var effective = ApplyOsOverride(kv.Value);
                return new CmdAllowedCommandInfo(
                    Name: kv.Key,
                    Description: effective.Description,
                    Shell: effective.Shell,
                    WorkingDirectory: ResolveWorkingDirectory(effective.WorkingDirectory),
                    Parameters: effective.Parameters.Keys.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase).ToArray());
            })
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

    public string BuildCmdRunToolDescription()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Runs one allowlisted command by name. Raw shell commands are not accepted; only preconfigured aliases may be executed. Commands execute within the default workspace. Returns a structured result with stdout, stderr, exit code, success flag, and error details if any.");

        if (_settings.AllowedCommands.Count == 0)
        {
            sb.AppendLine("Allowed commandName values: none configured.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("Allowed commandName values:");
        foreach (var commandEntry in _settings.AllowedCommands.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var command = ApplyOsOverride(commandEntry.Value);
            sb.Append("- ");
            sb.Append(commandEntry.Key);

            if (!string.IsNullOrWhiteSpace(command.Description))
            {
                sb.Append(": ");
                sb.Append(SingleLine(command.Description));
            }

            sb.Append(" Parameters: ");
            sb.Append(FormatParametersForDescription(command.Parameters));
            sb.AppendLine();
        }

        sb.Append("Pass parametersJson as a JSON object string using only the declared parameter names.");
        return sb.ToString().TrimEnd();
    }

    public string BuildListAllowedCommandsToolDescription()
    {
        var commands = ListAllowedCommands();
        var sb = new StringBuilder();
        sb.AppendLine("Lists the allowlisted commands that this secure command MCP server is allowed to execute. Takes no arguments; use input.request: {} or omit request.");
        sb.AppendLine("Output shape: { \"commands\": [ { \"name\": string, \"description\": string|null, \"shell\": string, \"workingDirectory\": string, \"parameters\": string[] } ] }.");

        if (commands.Count == 0)
        {
            sb.AppendLine("Current allowlisted commands: none configured.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("Current allowlisted commands:");
        foreach (var command in commands)
        {
            sb.Append("- ");
            sb.Append(command.Name);

            if (!string.IsNullOrWhiteSpace(command.Description))
            {
                sb.Append(": ");
                sb.Append(SingleLine(command.Description));
            }

            sb.Append("; shell=");
            sb.Append(command.Shell);
            sb.Append("; workingDirectory=");
            sb.Append(SingleLine(command.WorkingDirectory));
            sb.Append("; parameters=");
            sb.Append(FormatListForDescription(command.Parameters));
            sb.AppendLine(".");
        }

        sb.Append("For frozen workflow.plan requests, call cmd_run directly with one exact commandName above and pass parametersJson only when the chosen command declares parameters.");
        return sb.ToString().TrimEnd();
    }

    public string BuildGetPolicyToolDescription()
    {
        var policy = DescribePolicy();
        var sb = new StringBuilder();
        sb.AppendLine("Returns the active command execution policy. Takes no arguments; use input.request: {} or omit request.");
        sb.AppendLine("Output shape: { \"allowedShells\": string[], \"allowedWorkingRoots\": string[], \"defaultTimeoutMs\": integer, \"maxTimeoutMs\": integer, \"maxOutputCharacters\": integer, \"allowedCommandCount\": integer, \"environment\": { \"operatingSystem\": string, \"architecture\": string, \"machineName\": string, \"availableShells\": [ { \"name\": string, \"available\": boolean, \"resolvedPath\": string|null } ] } }.");
        sb.AppendLine("Current policy:");
        sb.AppendLine($"- allowedShells: {FormatListForDescription(policy.AllowedShells)}");
        sb.AppendLine($"- allowedWorkingRoots: {FormatListForDescription(policy.AllowedWorkingRoots)}");
        sb.AppendLine($"- defaultTimeoutMs: {policy.DefaultTimeoutMs}");
        sb.AppendLine($"- maxTimeoutMs: {policy.MaxTimeoutMs}");
        sb.AppendLine($"- maxOutputCharacters: {policy.MaxOutputCharacters}");
        sb.AppendLine($"- allowedCommandCount: {policy.AllowedCommandCount}");
        sb.AppendLine($"- environment.operatingSystem: {policy.Environment.OperatingSystem}");
        sb.AppendLine($"- environment.architecture: {policy.Environment.Architecture}");
        sb.AppendLine($"- environment.availableShells: {FormatShellAvailabilityForDescription(policy.Environment.AvailableShells)}");
        sb.Append("Only allowedWorkingRoots are authorized; generated workflows should not use paths outside those roots.");
        return sb.ToString().TrimEnd();
    }

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
            "sh" => ResolveKnownShell(normalized, ["/bin/sh", "sh"]),
            "bash" => ResolveKnownShell(normalized, ["/bin/bash", "bash"]),
            "zsh" => ResolveKnownShell(normalized, ["/bin/zsh", "zsh"]),
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
            ? _defaultWorkingDirectory
            : Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(basePath, configuredPath));

        var allowedRoots = ResolveAllowedWorkingRoots();
        if (!allowedRoots.Any(root => IsPathWithinRoot(candidate, root)))
            throw new InvalidOperationException(
                $"Working directory '{candidate}' is outside the allowed roots: {string.Join(", ", allowedRoots)}.");

        // Ensure the directory exists (creates it if possible).
        if (!Directory.Exists(candidate))
        {
            try
            {
                Directory.CreateDirectory(candidate);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Working directory '{candidate}' does not exist and could not be created: {ex.Message}", ex);
            }
        }

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

    public string RenderScript(AllowedCommandSettings command, JsonObject? parameters, string workingDirectory)
    {
        parameters ??= new JsonObject();
        ApplyLegacyArgsAlias(parameters, command);
        var normalizedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            normalizedParameters[parameter.Key] = parameter.Value.IsWorkspacePath
                ? ResolveWorkspacePathParameter(parameter.Key, value, parameter.Value, workingDirectory)
                : value;
        }

        var rendered = PlaceholderRegex.Replace(command.Script, match =>
        {
            var parameterName = match.Groups[1].Value;
            if (!normalizedParameters.TryGetValue(parameterName, out var parameterValue)
                || string.IsNullOrWhiteSpace(parameterValue))
            {
                throw new InvalidOperationException($"Missing value for placeholder '{parameterName}'.");
            }

            return EscapeForShell(command.Shell, parameterValue);
        });

        if (PlaceholderRegex.IsMatch(rendered))
            throw new InvalidOperationException("Command script still contains unresolved placeholders.");

        return rendered;
    }

    private static void ApplyLegacyArgsAlias(JsonObject parameters, AllowedCommandSettings command)
    {
        if (!parameters.TryGetPropertyValue("args", out var argsNode)
            || command.Parameters.ContainsKey("args"))
        {
            return;
        }

        // Backward compatibility: some clients send {"args":"<value>"}.
        // Accept it only when the command declares exactly one non-args parameter.
        if (command.Parameters.Count != 1)
        {
            throw new InvalidOperationException(
                "Parameter 'args' is not declared for this command. " +
                "Provide declared parameter names in parametersJson (for example {\"path\":\"...\"}).");
        }

        var targetParameter = command.Parameters.Keys.Single();
        if (parameters.ContainsKey(targetParameter))
            return;

        if (argsNode is null)
            throw new InvalidOperationException("Parameter 'args' must not be null.");

        if (argsNode is not JsonValue argsValue || !argsValue.TryGetValue<string>(out var legacyValue) || string.IsNullOrWhiteSpace(legacyValue))
        {
            throw new InvalidOperationException(
                "Parameter 'args' must be a non-empty JSON string when used as legacy alias.");
        }

        parameters[targetParameter] = legacyValue;
        parameters.Remove("args");
    }

    private static string FormatParametersForDescription(
        Dictionary<string, CommandParameterSettings> parameters)
    {
        if (parameters.Count == 0)
            return "none; omit parametersJson.";

        var parts = parameters
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var parameter = kv.Value;
                var required = parameter.Required ? "required" : "optional";
                var pathHint = parameter.IsWorkspacePath
                    ? $", workspace path, {parameter.PathKind.ToString().ToLowerInvariant()}"
                    : string.Empty;
                var description = string.IsNullOrWhiteSpace(parameter.Description)
                    ? string.Empty
                    : $", {SingleLine(parameter.Description)}";

                return $"{kv.Key} ({required}{pathHint}{description})";
            });

        return string.Join("; ", parts) + ".";
    }

    private static string FormatListForDescription(IEnumerable<string> values)
    {
        var items = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(SingleLine)
            .ToArray();

        return items.Length == 0 ? "none" : string.Join(", ", items);
    }

    private static string FormatShellAvailabilityForDescription(IEnumerable<CmdShellAvailability> shells)
    {
        var items = shells
            .Select(shell =>
            {
                var status = shell.Available ? "available" : "unavailable";
                return string.IsNullOrWhiteSpace(shell.ResolvedPath)
                    ? $"{shell.Name} ({status})"
                    : $"{shell.Name} ({status}, {SingleLine(shell.ResolvedPath)})";
            })
            .ToArray();

        return items.Length == 0 ? "none" : string.Join(", ", items);
    }

    private static string SingleLine(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string EscapeForShell(string shellName, string value)
    {
        var normalized = NormalizeRequiredValue(shellName, nameof(shellName)).ToLowerInvariant();
        return normalized switch
        {
            "powershell" => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'",
            "sh" or "bash" or "zsh" => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'",
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
        => GnOuGoWorkspace.DiscoverWorkspaceRoot(contentRootPath);

    internal static bool IsPathWithinRoot(string path, string root)
        => GnOuGoWorkspace.IsPathWithinRoot(path, root);

    private string ResolveWorkspacePathParameter(
        string parameterName,
        string parameterValue,
        CommandParameterSettings parameterSettings,
        string workingDirectory)
    {
        var normalizedValue = NormalizeRequiredValue(parameterValue, parameterName);

        if (normalizedValue.StartsWith('~'))
            throw new InvalidOperationException($"Parameter '{parameterName}' must not use home-directory shortcuts.");

        if (ContainsParentTraversalSegment(normalizedValue))
            throw new InvalidOperationException($"Parameter '{parameterName}' must not contain parent directory traversal segments ('..').");

        if (ContainsWildcardCharacters(normalizedValue))
            throw new InvalidOperationException($"Parameter '{parameterName}' must not contain wildcard characters.");

        if (HasDriveRelativePrefix(normalizedValue) && !Path.IsPathFullyQualified(normalizedValue))
            throw new InvalidOperationException($"Parameter '{parameterName}' must not use drive-relative paths.");

        if (!parameterSettings.AllowAbsolutePath && (Path.IsPathRooted(normalizedValue) || HasDriveRelativePrefix(normalizedValue)))
        {
            throw new InvalidOperationException($"Parameter '{parameterName}' must be a relative path inside the workspace.");
        }

        var candidatePath = Path.GetFullPath(Path.IsPathRooted(normalizedValue)
            ? normalizedValue
            : Path.Combine(workingDirectory, normalizedValue));

        var allowedRoots = ResolveAllowedWorkingRoots();
        if (!allowedRoots.Any(root => IsPathWithinRoot(candidatePath, root)))
        {
            throw new InvalidOperationException(
                $"Parameter '{parameterName}' resolves outside the allowed workspace roots: {string.Join(", ", allowedRoots)}.");
        }

        if (parameterSettings.MustExist)
            EnsureWorkspacePathExists(parameterName, candidatePath, parameterSettings.PathKind);

        return candidatePath;
    }

    private static void EnsureWorkspacePathExists(string parameterName, string candidatePath, WorkspacePathKind pathKind)
    {
        var exists = pathKind switch
        {
            WorkspacePathKind.File => File.Exists(candidatePath),
            WorkspacePathKind.Directory => Directory.Exists(candidatePath),
            _ => File.Exists(candidatePath) || Directory.Exists(candidatePath)
        };

        if (!exists)
            throw new InvalidOperationException($"Parameter '{parameterName}' points to a missing {pathKind.ToString().ToLowerInvariant()} path '{candidatePath}'.");
    }

    private static bool ContainsParentTraversalSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));

    private static bool ContainsWildcardCharacters(string path)
        => path.IndexOfAny(['*', '?']) >= 0;

    private static bool HasDriveRelativePrefix(string path)
        => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private List<string> ResolveAllowedWorkingRoots()
    {
        var basePath = _workspaceRootPath ?? _contentRootPath;
        var roots = new List<string> { _defaultWorkingDirectory };
        roots.AddRange(_settings.AllowedWorkingRoots
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(basePath, path))));

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ResolveDefaultWorkingDirectorySafe()
    {
        try
        {
            return ResolveDefaultWorkingDirectory();
        }
        catch (Exception ex)
        {
            var fallback = GnOuGoWorkspace.ResolveDefaultWorkingDirectorySafe(
                configuredPath: null,
                contentRootPath: _contentRootPath);
            Console.Error.WriteLine(
                $"[GnOuGo.Cmd.Mcp] WARNING: Could not resolve default Desktop working directory: {ex.Message}. " +
                $"Falling back to: {fallback}");
            return fallback;
        }
    }

    private string ResolveDefaultWorkingDirectory()
    {
        var configuredPath = string.IsNullOrWhiteSpace(_settings.DefaultWorkingDirectory)
            ? "GnOuGo"
            : _settings.DefaultWorkingDirectory.Trim();
        var desktopPath = GnOuGoWorkspace.ResolveDesktopDirectory();

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

    private static ShellLaunchInfo ResolveKnownShell(string logicalName, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryResolveExecutable(candidate, out var resolvedPath))
            {
                return logicalName switch
                {
                    "powershell" => new ShellLaunchInfo(logicalName, resolvedPath, script => $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
                    "sh" or "bash" or "zsh" => new ShellLaunchInfo(logicalName, resolvedPath, script => $"-c \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
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
