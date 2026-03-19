using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using GnOuGo.Flow.Browser;

var builder = BrowserHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.Configure<BrowserServerSettings>(
    builder.Configuration.GetSection(BrowserServerSettings.SectionName));
builder.Services.AddSingleton<PlaywrightBrowserHost>();
builder.Services.AddTransient<BrowserTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Flow.Browser",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<BrowserTools>();

var host = builder.Build();
var browserHost = host.Services.GetRequiredService<PlaywrightBrowserHost>();
var browserSettings = host.Services.GetRequiredService<IOptions<BrowserServerSettings>>().Value;
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Flow.Browser.Startup");

startupLogger.LogInformation(
    "Browser server configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, headless={Headless}, browserName={BrowserName}, channel={Channel}, slowMoMs={SlowMoMs}, holdOpenMs={HoldOpenMs}, keepBrowserOpen={KeepBrowserOpen}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    browserSettings.Headless,
    browserSettings.BrowserName,
    browserSettings.Channel,
    browserSettings.SlowMoMs,
    browserSettings.HoldOpenMs,
    browserSettings.KeepBrowserOpen);

try
{
    await host.RunAsync();
}
finally
{
    await browserHost.DisposeAsync();
}
