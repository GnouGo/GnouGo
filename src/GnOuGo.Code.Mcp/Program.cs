using GnOuGo.Code.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

var builder = CodeHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.Configure<CodeServerSettings>(
    builder.Configuration.GetSection(CodeServerSettings.SectionName));
builder.Services.AddSingleton<CodePolicy>();
builder.Services.AddSingleton<CodeProjectService>();
builder.Services.AddSingleton<GitRepositoryService>();
builder.Services.AddSingleton<ICodeAssistantClient, GitHubCopilotCodeClient>();
builder.Services.AddTransient<CodeTools>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Code.Mcp",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<CodeTools>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Code.Mcp.Startup");
var policy = host.Services.GetRequiredService<CodePolicy>();
var settings = host.Services.GetRequiredService<IOptions<CodeServerSettings>>().Value;
var info = policy.DescribePolicy();

logger.LogInformation(
    "Code MCP configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, defaultWorkingDirectory={DefaultWorkingDirectory}, allowedRoots={AllowedRoots}, allowedExtensions={AllowedExtensions}, allowWrites={AllowWrites}, gitAllowMutations={GitAllowMutations}, gitAllowNetworkOperations={GitAllowNetworkOperations}, copilotProvider={CopilotProvider}, copilotModel={CopilotModel}, copilotReasoningEffort={CopilotReasoningEffort}, hasToken={HasToken}, useLoggedInUser={UseLoggedInUser}, requestTimeoutSeconds={RequestTimeoutSeconds}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    info.DefaultWorkingDirectory,
    string.Join(", ", info.AllowedWorkingRoots),
    string.Join(", ", info.AllowedExtensions),
    info.AllowWrites,
    info.Git.AllowMutations,
    info.Git.AllowNetworkOperations,
    info.CopilotProvider,
    info.CopilotModel,
    settings.Copilot.ReasoningEffort,
    info.HasConfiguredToken,
    settings.Copilot.UseLoggedInUser,
    settings.Copilot.RequestTimeoutSeconds);

await host.RunAsync();



