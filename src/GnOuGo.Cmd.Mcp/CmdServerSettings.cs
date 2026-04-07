namespace GnOuGo.Cmd.Mcp;

public sealed class CmdServerSettings
{
    public const string SectionName = "Cmd";

    public string DefaultWorkingDirectory { get; set; } = "GnOuGo";
    public int DefaultTimeoutMs { get; set; } = 10_000;
    public int MaxTimeoutMs { get; set; } = 30_000;
    public int MaxOutputCharacters { get; set; } = 12_000;
    public List<string> AllowedShells { get; set; } = ["powershell", "sh", "cmd"];
    public List<string> AllowedWorkingRoots { get; set; } = [];
    public List<string> EnvironmentAllowList { get; set; } =
    [
        "PATH",
        "PATHEXT",
        "ComSpec",
        "SystemRoot",
        "WINDIR",
        "TMP",
        "TEMP",
        "USERPROFILE",
        "HOME"
    ];

    public Dictionary<string, AllowedCommandSettings> AllowedCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AllowedCommandSettings
{
    public string? Description { get; set; }
    public string Shell { get; set; } = "sh";
    public string Script { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public int? TimeoutMs { get; set; }
    public int? MaxOutputCharacters { get; set; }
    public Dictionary<string, CommandParameterSettings> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-OS overrides. Keys are "windows", "linux", "macos" (case-insensitive).
    /// Only Shell and Script are overridable; Parameters are merged (OS-specific values take precedence).
    /// </summary>
    public Dictionary<string, OsCommandOverride> OsOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// OS-specific override for an allowed command (Shell and/or Script).
/// </summary>
public sealed class OsCommandOverride
{
    public string? Shell { get; set; }
    public string? Script { get; set; }
    public Dictionary<string, CommandParameterSettings> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CommandParameterSettings
{
    public string? Description { get; set; }
    public bool Required { get; set; }
    public string Pattern { get; set; } = "^[A-Za-z0-9_./\\\\ -]{1,120}$";
    public int MaxLength { get; set; } = 120;
}

