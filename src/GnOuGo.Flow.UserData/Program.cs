using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using GnOuGo.Flow.UserData;

var builder = DataHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<InMemoryChatHistoryStore>();
builder.Services.AddTransient<DataTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Flow.UserData",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<DataTools>();

var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Flow.UserData.Startup");

startupLogger.LogInformation("GnOuGo.Flow.UserData MCP server started — chat history store ready.");

await host.RunAsync();
