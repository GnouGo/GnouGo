using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using GnOuGo.Cmd.Mcp;

var builder = CmdHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

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

startupLogger.LogInformation(
    "Cmd server configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, allowedShells={AllowedShells}, allowedRoots={AllowedRoots}, commandCount={CommandCount}, defaultTimeoutMs={DefaultTimeoutMs}, maxTimeoutMs={MaxTimeoutMs}, maxOutputCharacters={MaxOutputCharacters}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    string.Join(", ", settings.AllowedShells),
    string.Join(", ", settings.AllowedWorkingRoots),
    settings.AllowedCommands.Count,
    settings.DefaultTimeoutMs,
    settings.MaxTimeoutMs,
    settings.MaxOutputCharacters);

await host.RunAsync();

