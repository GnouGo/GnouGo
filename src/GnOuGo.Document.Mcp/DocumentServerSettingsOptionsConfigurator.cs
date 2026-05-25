using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GnOuGo.Document.Mcp;

internal sealed class DocumentServerSettingsOptionsConfigurator(IConfiguration configuration) : IConfigureOptions<DocumentServerSettings>
{
    public void Configure(DocumentServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var section = configuration.GetSection(DocumentServerSettings.SectionName);
        settings.DefaultWorkingDirectory = ReadString(section, nameof(DocumentServerSettings.DefaultWorkingDirectory), settings.DefaultWorkingDirectory);
        settings.MaxFileSizeBytes = ReadInt64(section, nameof(DocumentServerSettings.MaxFileSizeBytes), settings.MaxFileSizeBytes);
        settings.AllowedExtensions = ReadStringList(section, nameof(DocumentServerSettings.AllowedExtensions), settings.AllowedExtensions);
        settings.AllowedWorkingRoots = ReadStringList(section, nameof(DocumentServerSettings.AllowedWorkingRoots), settings.AllowedWorkingRoots);
    }

    private static string ReadString(IConfiguration section, string key, string currentValue)
        => section[key] ?? currentValue;

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

