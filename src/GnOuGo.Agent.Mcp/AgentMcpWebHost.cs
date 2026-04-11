using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Mcp;

public static class AgentMcpWebHost
{
    public static WebApplication Build(string[] args, string? urls = null, string routePrefix = AgentMcpHostingExtensions.DefaultRoutePrefix)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        var dbRelativePath = builder.Configuration.GetValue<string>("Agent:DatabasePath")
            ?? AgentMcpHostingExtensions.DefaultDatabasePath;
        var dbPath = Path.IsPathRooted(dbRelativePath)
            ? dbRelativePath
            : Path.Combine(AppContext.BaseDirectory, dbRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        builder.Services.AddAgentMcpPersistence(dbPath);
        builder.Services.AddAgentMcpHttpServer();

        var app = builder.Build();
        app.Services.InitializeAgentMcpAsync().GetAwaiter().GetResult();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapAgentMcp(routePrefix);

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GnOuGo.Agent.Mcp.Startup");
        logger.LogInformation(
            "GnOuGo.Agent.Mcp HTTP server configured — baseDirectory={BaseDirectory}, agentDb={AgentDbPath}, routePrefix={RoutePrefix}.",
            AppContext.BaseDirectory,
            dbPath,
            routePrefix);

        return app;
    }
}

