using System.Reflection;
using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server;

/// <summary>
/// Provides the application version extracted from the entry assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/>.
/// Registered as a singleton so Blazor components can inject it.
/// </summary>
public sealed class AppVersionInfo
{
    /// <summary>
    /// Full informational version string (e.g. "1.2.3-beta+abc1234" or "0.0.0-dev").
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Short display version (strips the +commitHash suffix if present).
    /// </summary>
    public string ShortVersion { get; }

    public AppVersionInfo()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Version = informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0-dev";

        // Strip the +commitHash suffix that .NET appends automatically
        var plusIndex = Version.IndexOf('+');
        ShortVersion = plusIndex > 0 ? Version[..plusIndex] : Version;
    }

    public AppVersionDto ToDto()
        => new(Version, ShortVersion);
}
