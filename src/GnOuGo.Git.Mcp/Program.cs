using GnOuGo.Git.Mcp;
using GnOuGo.Observability.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

var builder = GitHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.AddGnOuGoOpenTelemetry("GnOuGo.Git.Mcp");

builder.Services.Configure<GitServerSettings>(
    builder.Configuration.GetSection(GitServerSettings.SectionName));
builder.Services.AddSingleton<GitPolicy>();
builder.Services.AddSingleton<GitRepositoryService>();
builder.Services.AddTransient<GitTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Git.Mcp",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<GitTools>(GitMcpJson.SerializerOptions);

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Git.Mcp.Startup");
var policy = host.Services.GetRequiredService<GitPolicy>();
var settings = host.Services.GetRequiredService<IOptions<GitServerSettings>>().Value;
var info = policy.DescribePolicy();

logger.LogInformation(
    "Git MCP configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, defaultWorkingDirectory={DefaultWorkingDirectory}, allowedRoots={AllowedRoots}, allowMutations={AllowMutations}, allowNetworkOperations={AllowNetworkOperations}, requireCleanWorkingTreeForMerge={RequireCleanWorkingTreeForMerge}, maxDiffCharacters={MaxDiffCharacters}, maxLogCount={MaxLogCount}, defaultRemoteName={DefaultRemoteName}, hasToken={HasToken}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    info.DefaultWorkingDirectory,
    string.Join(", ", info.AllowedWorkingRoots),
    info.AllowMutations,
    info.AllowNetworkOperations,
    info.RequireCleanWorkingTreeForMerge,
    info.MaxDiffCharacters,
    info.MaxLogCount,
    settings.DefaultRemoteName,
    info.HasConfiguredToken);

await host.RunAsync();

