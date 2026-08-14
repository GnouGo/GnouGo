using GnOuGo.DocIngestor.Mcp.Models;
using GnOuGo.Observability.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GnOuGo.DocIngestor.Mcp;

public static class DocsIngestorMcpWebHost
{
    public static WebApplication Build(string[] args, string? urls = null, string routePrefix = DocsIngestorMcpHostingExtensions.DefaultRoutePrefix)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        if (args.Length > 0)
            builder.Configuration.AddCommandLine(args);

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.AddGnOuGoOpenTelemetry("GnOuGo.DocIngestor.Mcp");

        builder.Services.AddDocsIngestorMcpServices(builder.Configuration, AppContext.BaseDirectory);
        builder.Services.AddDocsIngestorMcpHttpServer();

        // Register source-generated JSON context for AOT-safe minimal-API serialization.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, DocsIngestorJsonContext.Default));

        var app = builder.Build();
        app.Services.InitializeDocsIngestorMcpAsync().GetAwaiter().GetResult();
        var paths = app.Services.GetRequiredService<DocsIngestorMcpStoragePaths>();

        app.Map("/health", static healthApp =>
        {
            healthApp.Run(static async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"status\":\"ok\"}", context.RequestAborted);
            });
        });
        app.MapDocsIngestorMcp(routePrefix);

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GnOuGo.DocIngestor.Mcp.Startup");
        logger.LogInformation(
            "GnOuGo.DocIngestor.Mcp HTTP server configured - baseDirectory={BaseDirectory}, metadataDb={MetadataDbPath}, vectorDb={VectorDbPath}, originals={OriginalsDirectory}, keyVaultDb={KeyVaultDbPath}, routePrefix={RoutePrefix}.",
            AppContext.BaseDirectory,
            paths.MetadataDatabasePath,
            paths.VectorDatabasePath,
            paths.OriginalsDirectory,
            paths.KeyVaultDatabasePath,
            routePrefix);

        return app;
    }

}
