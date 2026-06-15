using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using GnOuGo.Document.Mcp;
using GnOuGo.Mcp.Core;
using GnOuGo.Observability.Core;

var builder = DocumentHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.AddGnOuGoOpenTelemetry("GnOuGo.Document.Mcp");

builder.Services.AddSingleton<IConfigureOptions<DocumentServerSettings>, DocumentServerSettingsOptionsConfigurator>();
builder.Services.AddSingleton<DocumentPolicy>();
builder.Services.AddSingleton<DocumentOperationHost>();
builder.Services.AddTransient<DocumentTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Document.Mcp",
            Version = "1.0.0"
        };
        options.AddGnOuGoToolErrorNormalizer();
    })
    .WithStdioServerTransport()
    .WithTools<DocumentTools>(DocumentMcpJson.SerializerOptions);

var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Document.Mcp.Startup");
var policy = host.Services.GetRequiredService<DocumentPolicy>();
var policyInfo = policy.DescribePolicy();

startupLogger.LogInformation(
    "Document server started: defaultWorkingDirectory={DefaultWorkingDirectory}, allowedRoots={AllowedRoots}, allowedExtensions={AllowedExtensions}, maxFileSizeBytes={MaxFileSizeBytes}",
    policyInfo.DefaultWorkingDirectory,
    string.Join(", ", policyInfo.AllowedRoots),
    string.Join(", ", policyInfo.AllowedExtensions),
    policyInfo.MaxFileSizeBytes);

await host.RunAsync();
