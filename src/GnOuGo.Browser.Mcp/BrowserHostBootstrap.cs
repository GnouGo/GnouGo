using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GnOuGo.Browser.Mcp;

public static class BrowserHostBootstrap
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        ApplyVisualDebugDefaults(builder.Configuration, Debugger.IsAttached);
        return builder;
    }

    public static void ApplyVisualDebugDefaults(ConfigurationManager configuration, bool debuggerAttached)
    {
        if (!debuggerAttached)
            return;

        Dictionary<string, string?>? overrides = null;

        AddIfMissing("Browser:Headless", "false");
        AddIfMissing("Browser:SlowMoMs", "250");
        AddIfMissing("Browser:HoldOpenMs", "15000");
        AddIfMissing("Browser:KeepBrowserOpen", "true");

        if (overrides is not null)
            configuration.AddInMemoryCollection(overrides);

        void AddIfMissing(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                return;

            overrides ??= new Dictionary<string, string?>();
            overrides[key] = value;
        }
    }
}
