using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GnOuGo.Observability.Core;

internal sealed class OpenTelemetrySettingsOptionsConfigurator(IConfiguration configuration) : IConfigureOptions<OpenTelemetrySettings>
{
    public void Configure(OpenTelemetrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var section = configuration.GetSection(OpenTelemetrySettings.SectionName);
        settings.Enabled = ReadBoolean(section, nameof(OpenTelemetrySettings.Enabled), settings.Enabled);
        settings.ServiceName = ReadString(section, nameof(OpenTelemetrySettings.ServiceName), settings.ServiceName);
        settings.ServiceVersion = ReadString(section, nameof(OpenTelemetrySettings.ServiceVersion), settings.ServiceVersion);
        settings.OtlpEndpoint = ReadString(section, nameof(OpenTelemetrySettings.OtlpEndpoint), settings.OtlpEndpoint);
        settings.Protocol = ReadString(section, nameof(OpenTelemetrySettings.Protocol), settings.Protocol);
        settings.TenantId = ReadNullableString(section, nameof(OpenTelemetrySettings.TenantId), settings.TenantId);
        settings.IncludeLogs = ReadBoolean(section, nameof(OpenTelemetrySettings.IncludeLogs), settings.IncludeLogs);
        settings.IncludeMetrics = ReadBoolean(section, nameof(OpenTelemetrySettings.IncludeMetrics), settings.IncludeMetrics);
        settings.IncludeHttpClientInstrumentation = ReadBoolean(section, nameof(OpenTelemetrySettings.IncludeHttpClientInstrumentation), settings.IncludeHttpClientInstrumentation);
        settings.IncludeAspNetCoreTraces = ReadBoolean(section, nameof(OpenTelemetrySettings.IncludeAspNetCoreTraces), settings.IncludeAspNetCoreTraces);
        settings.ActivitySources = ReadStringArray(section, nameof(OpenTelemetrySettings.ActivitySources), settings.ActivitySources);
        settings.Meters = ReadStringArray(section, nameof(OpenTelemetrySettings.Meters), settings.Meters);
    }

    public static OpenTelemetrySettings Read(IConfiguration configuration)
    {
        var settings = new OpenTelemetrySettings();
        new OpenTelemetrySettingsOptionsConfigurator(configuration).Configure(settings);
        return settings;
    }

    private static string ReadString(IConfiguration section, string key, string currentValue)
        => section[key] ?? currentValue;

    private static string? ReadNullableString(IConfiguration section, string key, string? currentValue)
        => section[key] ?? currentValue;

    private static bool ReadBoolean(IConfiguration section, string key, bool currentValue)
        => bool.TryParse(section[key], out var value) ? value : currentValue;

    private static string[] ReadStringArray(IConfiguration section, string key, string[] currentValue)
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
            .ToArray();
    }
}


