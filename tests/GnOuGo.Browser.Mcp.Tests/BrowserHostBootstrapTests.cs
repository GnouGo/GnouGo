using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public class BrowserHostBootstrapTests
{
    private const string PlaywrightBrowsersPathEnvironmentVariable = "PLAYWRIGHT_BROWSERS_PATH";

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

    [Fact]
    public void ConfigureBundledPlaywrightBrowserPath_UsesLocalMsPlaywrightDirectory_WhenEnvironmentIsMissing()
    {
        var previous = Environment.GetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "gnougo-browser-bootstrap-" + Guid.NewGuid().ToString("N"));
        var browsers = Path.Combine(root, "ms-playwright");
        Directory.CreateDirectory(browsers);
        File.WriteAllText(Path.Combine(browsers, "marker.txt"), "present");

        try
        {
            Environment.SetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable, null);

            var resolved = BrowserHostBootstrap.ConfigureBundledPlaywrightBrowserPath(root);

            Assert.Equal(browsers, resolved);
            Assert.Equal(browsers, Environment.GetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void ConfigureBundledPlaywrightBrowserPath_PreservesExplicitEnvironmentValue()
    {
        var previous = Environment.GetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable, "custom-playwright-path");

            var resolved = BrowserHostBootstrap.ConfigureBundledPlaywrightBrowserPath(AppContext.BaseDirectory);

            Assert.Equal("custom-playwright-path", resolved);
            Assert.Equal("custom-playwright-path", Environment.GetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlaywrightBrowsersPathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task PlaywrightBrowserHost_DisposeAsync_IsIdempotent()
    {
        var host = new PlaywrightBrowserHost(
            Options.Create(new BrowserServerSettings()),
            NullLogger<PlaywrightBrowserHost>.Instance);

        await host.DisposeAsync();
        await host.DisposeAsync();
    }
}

