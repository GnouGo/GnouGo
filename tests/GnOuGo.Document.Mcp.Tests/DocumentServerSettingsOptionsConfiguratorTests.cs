using Microsoft.Extensions.Configuration;
using Xunit;

namespace GnOuGo.Document.Mcp.Tests;

public sealed class DocumentServerSettingsOptionsConfiguratorTests
{
    [Fact]
    public void Configure_AppliesConfiguredValuesWithoutConfigurationBinder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Document:DefaultWorkingDirectory"] = "C:/workspace",
                ["Document:MaxFileSizeBytes"] = "1048576",
                ["Document:AllowedExtensions:0"] = ".md",
                ["Document:AllowedExtensions:1"] = ".txt",
                ["Document:AllowedWorkingRoots:0"] = "C:/workspace",
                ["Document:AllowedWorkingRoots:1"] = "D:/shared"
            })
            .Build();

        var settings = new DocumentServerSettings();
        new DocumentServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Equal("C:/workspace", settings.DefaultWorkingDirectory);
        Assert.Equal(1_048_576, settings.MaxFileSizeBytes);
        Assert.Equal([".md", ".txt"], settings.AllowedExtensions);
        Assert.Equal(["C:/workspace", "D:/shared"], settings.AllowedWorkingRoots);
    }

    [Fact]
    public void Configure_PreservesDefaultsWhenValuesAreMissingOrInvalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Document:MaxFileSizeBytes"] = "not-a-number"
            })
            .Build();

        var settings = new DocumentServerSettings();
        var defaultExtensions = settings.AllowedExtensions.ToArray();

        new DocumentServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Equal(50 * 1024 * 1024, settings.MaxFileSizeBytes);
        Assert.Equal(defaultExtensions, settings.AllowedExtensions);
        Assert.Empty(settings.AllowedWorkingRoots);
    }
}

