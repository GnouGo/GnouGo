using GnOuGo.KeyVault.Core;
using GnOuGo.Observability.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GnOuGo.DocIngestor.Mcp;

public static class DocsIngestorMcpWebHost
{
    public static WebApplication Build(string[] args, string? urls = null, string routePrefix = DocsIngestorMcpHostingExtensions.DefaultRoutePrefix)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.AddGnOuGoOpenTelemetry("GnOuGo.DocIngestor.Mcp");

        builder.Services.Configure<DocsIngestorMcpOptions>(builder.Configuration.GetSection("DocsIngestorMcp"));
        var options = builder.Configuration.GetSection("DocsIngestorMcp").Get<DocsIngestorMcpOptions>() ?? new();

        var metadataDbPath = DocsIngestorMcpPathResolver.Resolve(options.DatabasePath, AppContext.BaseDirectory, "data/gnougo-docs-ingestor-mcp.db");
        var vectorDbPath = DocsIngestorMcpPathResolver.Resolve(options.VectorDatabasePath, AppContext.BaseDirectory, "data/gnougo-docs-ingestor-vectors.sqlite");
        var originalsDirectory = DocsIngestorMcpPathResolver.Resolve(options.OriginalsDirectory, AppContext.BaseDirectory, "data/docs-ingestor/originals");
        var keyVaultDbRelativePath = builder.Configuration.GetValue<string>("KeyVault:DatabasePath") ?? KeyVaultDatabasePathResolver.DefaultRelativePath;
        var keyVaultDbPath = KeyVaultDatabasePathResolver.Resolve(keyVaultDbRelativePath, AppContext.BaseDirectory);

        Directory.CreateDirectory(Path.GetDirectoryName(metadataDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(vectorDbPath)!);
        Directory.CreateDirectory(originalsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(keyVaultDbPath)!);

        builder.Services.AddDocsIngestorCoreServices(metadataDbPath, vectorDbPath, originalsDirectory, keyVaultDbPath);
        builder.Services.AddDocsIngestorMcpHttpServer();

        var app = builder.Build();
        app.Services.InitializeDocsIngestorMcpAsync().GetAwaiter().GetResult();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapDocsIngestorMcp(routePrefix);

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GnOuGo.DocIngestor.Mcp.Startup");
        logger.LogInformation(
            "GnOuGo.DocIngestor.Mcp HTTP server configured � baseDirectory={BaseDirectory}, metadataDb={MetadataDbPath}, vectorDb={VectorDbPath}, originals={OriginalsDirectory}, keyVaultDb={KeyVaultDbPath}, routePrefix={RoutePrefix}.",
            AppContext.BaseDirectory,
            metadataDbPath,
            vectorDbPath,
            originalsDirectory,
            keyVaultDbPath,
            routePrefix);

        return app;
    }
}

