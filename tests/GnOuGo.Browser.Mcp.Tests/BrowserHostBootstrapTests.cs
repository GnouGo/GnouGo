using Microsoft.Extensions.Configuration;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public class BrowserHostBootstrapTests
{
    [Fact]
    public void CreateBuilder_UsesAppContextBaseDirectoryAsContentRoot()
    {
        var builder = BrowserHostBootstrap.CreateBuilder([]);

        Assert.Equal(AppContext.BaseDirectory, builder.Environment.ContentRootPath);
    }

    [Fact]
    public void ApplyVisualDebugDefaults_AddsVisibleBrowserDefaults_WhenDebuggerAttachedAndKeysMissing()
    {
        var configuration = new ConfigurationManager();

        BrowserHostBootstrap.ApplyVisualDebugDefaults(configuration, debuggerAttached: true);

        Assert.Equal("false", configuration["Browser:Headless"]);
        Assert.Equal("250", configuration["Browser:SlowMoMs"]);
        Assert.Equal("15000", configuration["Browser:HoldOpenMs"]);
        Assert.Equal("true", configuration["Browser:KeepBrowserOpen"]);
    }

    [Fact]
    public void ApplyVisualDebugDefaults_DoesNotOverrideExistingConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Browser:Headless"] = "true",
            ["Browser:SlowMoMs"] = "4000",
            ["Browser:HoldOpenMs"] = "4000",
            ["Browser:KeepBrowserOpen"] = "false"
        });

        BrowserHostBootstrap.ApplyVisualDebugDefaults(configuration, debuggerAttached: true);

        Assert.Equal("true", configuration["Browser:Headless"]);
        Assert.Equal("4000", configuration["Browser:SlowMoMs"]);
        Assert.Equal("4000", configuration["Browser:HoldOpenMs"]);
        Assert.Equal("false", configuration["Browser:KeepBrowserOpen"]);
    }

    [Fact]
    public void ApplyVisualDebugDefaults_DoesNothing_WhenDebuggerIsNotAttached()
    {
        var configuration = new ConfigurationManager();

        BrowserHostBootstrap.ApplyVisualDebugDefaults(configuration, debuggerAttached: false);

        Assert.Null(configuration["Browser:Headless"]);
        Assert.Null(configuration["Browser:SlowMoMs"]);
        Assert.Null(configuration["Browser:HoldOpenMs"]);
        Assert.Null(configuration["Browser:KeepBrowserOpen"]);
    }
}

