using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using GnOuGo.Cmd.Mcp;
using GnOuGo.Observability.Core;

var builder = CmdHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.AddGnOuGoOpenTelemetry("GnOuGo.Cmd.Mcp");

builder.Services.Configure<CmdServerSettings>(
    builder.Configuration.GetSection(CmdServerSettings.SectionName));
builder.Services.AddSingleton<CommandPolicy>();
builder.Services.AddSingleton<CommandExecutionHost>();
builder.Services.AddTransient<CmdTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Cmd.Mcp",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<CmdTools>();

var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Cmd.Mcp.Startup");
var settings = host.Services.GetRequiredService<IOptions<CmdServerSettings>>().Value;
var policy = host.Services.GetRequiredService<CommandPolicy>();
var defaultWorkingDirectory = policy.ResolveWorkingDirectory(null);
var allowedWorkingRoots = policy.DescribePolicy().AllowedWorkingRoots;

startupLogger.LogInformation(
    "Cmd server configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, configuredDefaultWorkingDirectory={ConfiguredDefaultWorkingDirectory}, resolvedDefaultWorkingDirectory={ResolvedDefaultWorkingDirectory}, allowedShells={AllowedShells}, allowedRoots={AllowedRoots}, commandCount={CommandCount}, defaultTimeoutMs={DefaultTimeoutMs}, maxTimeoutMs={MaxTimeoutMs}, maxOutputCharacters={MaxOutputCharacters}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    settings.DefaultWorkingDirectory,
    defaultWorkingDirectory,
    string.Join(", ", settings.AllowedShells),
    string.Join(", ", allowedWorkingRoots),
    settings.AllowedCommands.Count,
    settings.DefaultTimeoutMs,
    settings.MaxTimeoutMs,
    settings.MaxOutputCharacters);

await host.RunAsync();

