using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using GnOuGo.Browser.Mcp;
using GnOuGo.Mcp.Core;
using GnOuGo.Observability.Core;

AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    if (args.ExceptionObject is Exception ex)
        Console.Error.WriteLine($"[GnOuGo.Browser.Mcp] Unhandled exception: {ex}");
    else
        Console.Error.WriteLine($"[GnOuGo.Browser.Mcp] Unhandled exception object: {args.ExceptionObject}");
};

var builder = BrowserHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.AddGnOuGoOpenTelemetry("GnOuGo.Browser.Mcp");

builder.Services.Configure<BrowserServerSettings>(
    builder.Configuration.GetSection(BrowserServerSettings.SectionName));
builder.Services.AddSingleton<PlaywrightBrowserHost>();
builder.Services.AddTransient<BrowserTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Browser.Mcp",
            Version = "1.0.0"
        };
        options.AddGnOuGoToolErrorNormalizer();
    })
    .WithStdioServerTransport()
    .WithTools<BrowserTools>(BrowserMcpJson.SerializerOptions);

var host = builder.Build();
var browserHost = host.Services.GetRequiredService<PlaywrightBrowserHost>();
var browserSettings = host.Services.GetRequiredService<IOptions<BrowserServerSettings>>().Value;
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Browser.Mcp.Startup");

startupLogger.LogInformation(
    "Browser server configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, headless={Headless}, browserName={BrowserName}, channel={Channel}, slowMoMs={SlowMoMs}, holdOpenMs={HoldOpenMs}, keepBrowserOpen={KeepBrowserOpen}, correlationId={CorrelationId}, runId={RunId}, traceparent={TraceParent}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    browserSettings.Headless,
    browserSettings.BrowserName,
    browserSettings.Channel,
    browserSettings.SlowMoMs,
    browserSettings.HoldOpenMs,
    browserSettings.KeepBrowserOpen,
    Environment.GetEnvironmentVariable("GNouGo__CorrelationId"),
    Environment.GetEnvironmentVariable("GNouGo__RunId"),
    Environment.GetEnvironmentVariable("GNouGo__TraceParent"));

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[GnOuGo.Browser.Mcp] Fatal host failure: {ex}");
    throw;
}
finally
{
    await browserHost.DisposeAsync();
}
