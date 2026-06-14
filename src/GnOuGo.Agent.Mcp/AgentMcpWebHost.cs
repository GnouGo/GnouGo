using GnOuGo.Observability.Core;
using System.Text.Json;

namespace GnOuGo.Agent.Mcp;

public static class AgentMcpWebHost
{
    public static WebApplication Build(string[] args, string? urls = null, string routePrefix = AgentMcpHostingExtensions.DefaultRoutePrefix)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        if (args.Length > 0)
            builder.Configuration.AddCommandLine(args);

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.AddGnOuGoOpenTelemetry("GnOuGo.Agent.Mcp");

        var dbRelativePath = builder.Configuration["Agent:DatabasePath"]
            ?? AgentMcpHostingExtensions.DefaultDatabasePath;
        var dbPath = AgentMcpHostingExtensions.ResolveDatabasePath(dbRelativePath, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        builder.Services.AddAgentMcpPersistence(dbPath);
        builder.Services.AddAgentMcpHttpServer();

        var app = builder.Build();
        app.Services.InitializeAgentMcpAsync().GetAwaiter().GetResult();

        app.Use(static async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method)
                && context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHealthResponseAsync(context);
                return;
            }

            await next(context);
        });
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

    private static async Task WriteHealthResponseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new HealthResponse("ok"),
            AgentMcpJsonContext.Default.HealthResponse);

        await context.Response.Body.WriteAsync(payload, context.RequestAborted);
    }
}
