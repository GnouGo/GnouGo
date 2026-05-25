using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GnOuGo.Cmd.Mcp;

internal sealed class CmdServerSettingsOptionsConfigurator(IConfiguration configuration) : IConfigureOptions<CmdServerSettings>
{
    public void Configure(CmdServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var section = configuration.GetSection(CmdServerSettings.SectionName);
        settings.DefaultWorkingDirectory = ReadString(section, nameof(CmdServerSettings.DefaultWorkingDirectory), settings.DefaultWorkingDirectory);
        settings.DefaultTimeoutMs = ReadInt32(section, nameof(CmdServerSettings.DefaultTimeoutMs), settings.DefaultTimeoutMs);
        settings.MaxTimeoutMs = ReadInt32(section, nameof(CmdServerSettings.MaxTimeoutMs), settings.MaxTimeoutMs);
        settings.MaxOutputCharacters = ReadInt32(section, nameof(CmdServerSettings.MaxOutputCharacters), settings.MaxOutputCharacters);
        settings.AllowedShells = ReadStringList(section, nameof(CmdServerSettings.AllowedShells), settings.AllowedShells);
        settings.AllowedWorkingRoots = ReadStringList(section, nameof(CmdServerSettings.AllowedWorkingRoots), settings.AllowedWorkingRoots);
        settings.EnvironmentAllowList = ReadStringList(section, nameof(CmdServerSettings.EnvironmentAllowList), settings.EnvironmentAllowList);
        settings.AllowedCommands = ReadAllowedCommands(section, nameof(CmdServerSettings.AllowedCommands), settings.AllowedCommands);
    }

    private static Dictionary<string, AllowedCommandSettings> ReadAllowedCommands(
        IConfiguration section,
        string key,
        Dictionary<string, AllowedCommandSettings> currentValue)
    {
        var commandsSection = section.GetSection(key);
        var commandNodes = commandsSection.GetChildren().ToArray();
        if (commandNodes.Length == 0)
        {
            return currentValue;
        }

        var commands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var commandNode in commandNodes)
        {
            if (string.IsNullOrWhiteSpace(commandNode.Key))
            {
                continue;
            }

            var commandSettings = new AllowedCommandSettings();
            commandSettings.Description = ReadNullableString(commandNode, nameof(AllowedCommandSettings.Description), commandSettings.Description);
            commandSettings.Shell = ReadString(commandNode, nameof(AllowedCommandSettings.Shell), commandSettings.Shell);
            commandSettings.Script = ReadString(commandNode, nameof(AllowedCommandSettings.Script), commandSettings.Script);
            commandSettings.WorkingDirectory = ReadNullableString(commandNode, nameof(AllowedCommandSettings.WorkingDirectory), commandSettings.WorkingDirectory);
            commandSettings.TimeoutMs = ReadNullableInt32(commandNode, nameof(AllowedCommandSettings.TimeoutMs), commandSettings.TimeoutMs);
            commandSettings.MaxOutputCharacters = ReadNullableInt32(commandNode, nameof(AllowedCommandSettings.MaxOutputCharacters), commandSettings.MaxOutputCharacters);
            commandSettings.Parameters = ReadCommandParameters(commandNode, nameof(AllowedCommandSettings.Parameters), commandSettings.Parameters);
            commandSettings.OsOverrides = ReadOsOverrides(commandNode, nameof(AllowedCommandSettings.OsOverrides), commandSettings.OsOverrides);

            commands[commandNode.Key] = commandSettings;
        }

        return commands;
    }

    private static Dictionary<string, OsCommandOverride> ReadOsOverrides(
        IConfiguration section,
        string key,
        Dictionary<string, OsCommandOverride> currentValue)
    {
        var overridesSection = section.GetSection(key);
        var overrideNodes = overridesSection.GetChildren().ToArray();
        if (overrideNodes.Length == 0)
        {
            return currentValue;
        }

        var overrides = new Dictionary<string, OsCommandOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var overrideNode in overrideNodes)
        {
            if (string.IsNullOrWhiteSpace(overrideNode.Key))
            {
                continue;
            }

            var osOverride = new OsCommandOverride
            {
                Shell = ReadNullableString(overrideNode, nameof(OsCommandOverride.Shell), null),
                Script = ReadNullableString(overrideNode, nameof(OsCommandOverride.Script), null),
                Parameters = ReadCommandParameters(overrideNode, nameof(OsCommandOverride.Parameters), new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase))
            };

            overrides[overrideNode.Key] = osOverride;
        }

        return overrides;
    }

    private static Dictionary<string, CommandParameterSettings> ReadCommandParameters(
        IConfiguration section,
        string key,
        Dictionary<string, CommandParameterSettings> currentValue)
    {
        var parametersSection = section.GetSection(key);
        var parameterNodes = parametersSection.GetChildren().ToArray();
        if (parameterNodes.Length == 0)
        {
            return currentValue;
        }

        var parameters = new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameterNode in parameterNodes)
        {
            if (string.IsNullOrWhiteSpace(parameterNode.Key))
            {
                continue;
            }

            var parameterSettings = new CommandParameterSettings();
            parameterSettings.Description = ReadNullableString(parameterNode, nameof(CommandParameterSettings.Description), parameterSettings.Description);
            parameterSettings.Required = ReadBoolean(parameterNode, nameof(CommandParameterSettings.Required), parameterSettings.Required);
            parameterSettings.Pattern = ReadString(parameterNode, nameof(CommandParameterSettings.Pattern), parameterSettings.Pattern);
            parameterSettings.MaxLength = ReadInt32(parameterNode, nameof(CommandParameterSettings.MaxLength), parameterSettings.MaxLength);
            parameterSettings.IsWorkspacePath = ReadBoolean(parameterNode, nameof(CommandParameterSettings.IsWorkspacePath), parameterSettings.IsWorkspacePath);
            parameterSettings.AllowAbsolutePath = ReadBoolean(parameterNode, nameof(CommandParameterSettings.AllowAbsolutePath), parameterSettings.AllowAbsolutePath);
            parameterSettings.MustExist = ReadBoolean(parameterNode, nameof(CommandParameterSettings.MustExist), parameterSettings.MustExist);
            parameterSettings.PathKind = ReadEnum(parameterNode, nameof(CommandParameterSettings.PathKind), parameterSettings.PathKind);

            parameters[parameterNode.Key] = parameterSettings;
        }

        return parameters;
    }

    private static string ReadString(IConfiguration section, string key, string currentValue)
        => section[key] ?? currentValue;

    private static string? ReadNullableString(IConfiguration section, string key, string? currentValue)
        => section[key] ?? currentValue;

    private static bool ReadBoolean(IConfiguration section, string key, bool currentValue)
        => bool.TryParse(section[key], out var value) ? value : currentValue;

    private static int ReadInt32(IConfiguration section, string key, int currentValue)
        => int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : currentValue;

    private static int? ReadNullableInt32(IConfiguration section, string key, int? currentValue)
        => int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : currentValue;

    private static TEnum ReadEnum<TEnum>(IConfiguration section, string key, TEnum currentValue)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(section[key], ignoreCase: true, out var value) ? value : currentValue;

    private static List<string> ReadStringList(IConfiguration section, string key, List<string> currentValue)
    {
        var children = section.GetSection(key).GetChildren().ToArray();
        if (children.Length == 0)
        {
            return currentValue;
        }

        return children
            .Select(child => child.Value)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
    }
}

