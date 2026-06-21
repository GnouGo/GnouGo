using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using GnOuGo.Cmd.Mcp;
using GnOuGo.Mcp.Core;
using GnOuGo.Observability.Core;

try
{
    var builder = CmdHostBootstrap.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    try
    {
        builder.AddGnOuGoOpenTelemetry("GnOuGo.Cmd.Mcp");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[GnOuGo.Cmd.Mcp] WARNING: OpenTelemetry initialization failed (non-fatal): {ex.Message}");
    }

    builder.Services.AddSingleton<IConfigureOptions<CmdServerSettings>, CmdServerSettingsOptionsConfigurator>();
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
            options.AddGnOuGoToolErrorNormalizer();
            options.Filters.Request.ListToolsFilters.Add(next => async (request, cancellationToken) =>
            {
                var result = await next(request, cancellationToken);
                var policy = request.Services?.GetService<CommandPolicy>();
                if (policy is null)
                    return result;

                foreach (var tool in result.Tools)
                {
                    if (string.Equals(tool.Name, "cmd_run", StringComparison.Ordinal))
                        tool.Description = policy.BuildCmdRunToolDescription();
                }

                return result;
            });
        })
        .WithStdioServerTransport()
        .WithTools<CmdTools>(CmdMcpJson.SerializerOptions);

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
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[GnOuGo.Cmd.Mcp] FATAL: Unhandled exception during startup or execution:");
    Console.Error.WriteLine(ex.ToString());
    Environment.Exit(1);
}
