using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CodeServerSettingsOptionsConfigurator(IConfiguration configuration) : IConfigureOptions<CodeServerSettings>
{
    public void Configure(CodeServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var section = configuration.GetSection(CodeServerSettings.SectionName);
        settings.DefaultWorkingDirectory = ReadString(section, nameof(CodeServerSettings.DefaultWorkingDirectory), settings.DefaultWorkingDirectory);
        settings.MaxFileSizeBytes = ReadInt64(section, nameof(CodeServerSettings.MaxFileSizeBytes), settings.MaxFileSizeBytes);
        settings.MaxSearchResults = ReadInt32(section, nameof(CodeServerSettings.MaxSearchResults), settings.MaxSearchResults);
        settings.MaxPromptCharacters = ReadInt32(section, nameof(CodeServerSettings.MaxPromptCharacters), settings.MaxPromptCharacters);
        settings.AllowWrites = ReadBoolean(section, nameof(CodeServerSettings.AllowWrites), settings.AllowWrites);
        settings.AllowedWorkingRoots = ReadStringList(section, nameof(CodeServerSettings.AllowedWorkingRoots), settings.AllowedWorkingRoots);
        settings.AllowedExtensions = ReadStringList(section, nameof(CodeServerSettings.AllowedExtensions), settings.AllowedExtensions);

        ConfigureCopilot(section.GetSection(nameof(CodeServerSettings.Copilot)), settings.Copilot);
    }

    private static void ConfigureCopilot(IConfigurationSection section, CodeCopilotSettings settings)
    {
        settings.Provider = ReadString(section, nameof(CodeCopilotSettings.Provider), settings.Provider);
        settings.Model = ReadString(section, nameof(CodeCopilotSettings.Model), settings.Model);
        settings.Mode = ReadString(section, nameof(CodeCopilotSettings.Mode), settings.Mode);
        settings.ReasoningEffort = ReadNullableString(section, nameof(CodeCopilotSettings.ReasoningEffort), settings.ReasoningEffort);
        settings.Endpoint = ReadString(section, nameof(CodeCopilotSettings.Endpoint), settings.Endpoint);
        settings.ApiKey = ReadNullableString(section, nameof(CodeCopilotSettings.ApiKey), settings.ApiKey);
        settings.UseLoggedInUser = ReadBoolean(section, nameof(CodeCopilotSettings.UseLoggedInUser), settings.UseLoggedInUser);
        settings.ForwardTraceContext = ReadBoolean(section, nameof(CodeCopilotSettings.ForwardTraceContext), settings.ForwardTraceContext);
        settings.LogLevel = ReadString(section, nameof(CodeCopilotSettings.LogLevel), settings.LogLevel);
        settings.RequestTimeoutSeconds = ReadInt32(section, nameof(CodeCopilotSettings.RequestTimeoutSeconds), settings.RequestTimeoutSeconds);
        settings.TokenEnvironmentVariables = ReadStringList(section, nameof(CodeCopilotSettings.TokenEnvironmentVariables), settings.TokenEnvironmentVariables);

        ConfigureTelemetry(section.GetSection(nameof(CodeCopilotSettings.Telemetry)), settings.Telemetry);
    }

    private static void ConfigureTelemetry(IConfigurationSection section, CodeCopilotTelemetrySettings settings)
    {
        settings.Enabled = ReadBoolean(section, nameof(CodeCopilotTelemetrySettings.Enabled), settings.Enabled);
        settings.ExporterType = ReadString(section, nameof(CodeCopilotTelemetrySettings.ExporterType), settings.ExporterType);
        settings.OtlpEndpoint = ReadNullableString(section, nameof(CodeCopilotTelemetrySettings.OtlpEndpoint), settings.OtlpEndpoint);
        settings.FilePath = ReadNullableString(section, nameof(CodeCopilotTelemetrySettings.FilePath), settings.FilePath);
        settings.SourceName = ReadString(section, nameof(CodeCopilotTelemetrySettings.SourceName), settings.SourceName);
        settings.CaptureContent = ReadBoolean(section, nameof(CodeCopilotTelemetrySettings.CaptureContent), settings.CaptureContent);
    }

    private static string ReadString(IConfiguration section, string key, string currentValue)
        => section[key] ?? currentValue;

    private static string? ReadNullableString(IConfiguration section, string key, string? currentValue)
        => section[key] ?? currentValue;

    private static bool ReadBoolean(IConfiguration section, string key, bool currentValue)
        => bool.TryParse(section[key], out var value) ? value : currentValue;

    private static int ReadInt32(IConfiguration section, string key, int currentValue)
        => int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : currentValue;

    private static long ReadInt64(IConfiguration section, string key, long currentValue)
        => long.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : currentValue;

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

