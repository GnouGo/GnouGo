using GnOuGo.Git.Mcp;
using GnOuGo.Mcp.Core;
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
        options.AddGnOuGoToolErrorNormalizer();
        options.Filters.Request.ListToolsFilters.Add(next => async (request, ct) =>
        {
            var result = await next(request, ct);
            foreach (var tool in result.Tools)
            {
                GitCloneTargetContract.Publish(tool);
                McpEffectMetadata.Publish(tool, tool.Name switch
                {
                    "git_get_policy" or "git_repository_info" or "git_status" or "git_diff" or "git_compare_refs" or "git_log" or "git_branches" or "git_conflicts" => "read",
                    "git_clone" or "git_fetch" or "git_pull" or "git_push" or "git_delete_remote_branch" or "git_create_branch" or "git_delete_branch" or
                    "git_checkout" or "git_switch_branch" or "git_stage" or "git_unstage" or "git_commit" or "git_merge" or "git_resolve_conflict" => "write",
                    _ => null
                });
            }
            return result;
        });
    })
    .WithStdioServerTransport()
    .WithTools<GitTools>(GitMcpJson.SerializerOptions);

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Git.Mcp.Startup");
var policy = host.Services.GetRequiredService<GitPolicy>();
var settings = host.Services.GetRequiredService<IOptions<GitServerSettings>>().Value;
var info = policy.DescribePolicy();

logger.LogInformation(
    "Git MCP configuration: contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, defaultWorkingDirectory={DefaultWorkingDirectory}, allowedRoots={AllowedRoots}, allowMutations={AllowMutations}, allowNetworkOperations={AllowNetworkOperations}, reviewReadOnly={ReviewReadOnly}, requireCleanWorkingTreeForMerge={RequireCleanWorkingTreeForMerge}, maxDiffCharacters={MaxDiffCharacters}, maxLogCount={MaxLogCount}, defaultRemoteName={DefaultRemoteName}, hasToken={HasToken}",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    info.DefaultWorkingDirectory,
    string.Join(", ", info.AllowedWorkingRoots),
    info.AllowMutations,
    info.AllowNetworkOperations,
    info.ReviewReadOnly,
    info.RequireCleanWorkingTreeForMerge,
    info.MaxDiffCharacters,
    info.MaxLogCount,
    settings.DefaultRemoteName,
    info.HasConfiguredToken);

await host.RunAsync();
