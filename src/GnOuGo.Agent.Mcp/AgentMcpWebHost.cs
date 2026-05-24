using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GnOuGo.Observability.Core;

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
        builder.AddGnOuGoOpenTelemetry("GnOuGo.Agent.Mcp");

        var dbRelativePath = builder.Configuration.GetValue<string>("Agent:DatabasePath")
            ?? AgentMcpHostingExtensions.DefaultDatabasePath;
        var dbPath = AgentMcpHostingExtensions.ResolveDatabasePath(dbRelativePath, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        builder.Services.AddAgentMcpPersistence(dbPath);
        builder.Services.AddAgentMcpHttpServer();

        var app = builder.Build();
        app.Services.InitializeAgentMcpAsync().GetAwaiter().GetResult();

        app.MapGet("/health", (RequestDelegate)WriteHealthAsync);
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

    private static Task WriteHealthAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync("{\"status\":\"ok\"}");
    }
}

