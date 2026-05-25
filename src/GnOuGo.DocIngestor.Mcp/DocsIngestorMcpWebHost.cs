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

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.AddGnOuGoOpenTelemetry("GnOuGo.DocIngestor.Mcp");

        builder.Services.Configure<DocsIngestorMcpOptions>(options => BindOptions(builder.Configuration, options));

        // Read individual config values to avoid reflection-based Get<T>() at startup.
        var metadataDbPath = DocsIngestorMcpPathResolver.Resolve(
            builder.Configuration["DocsIngestorMcp:DatabasePath"],
            AppContext.BaseDirectory, "data/gnougo-docs-ingestor-mcp.db");
        var vectorDbPath = DocsIngestorMcpPathResolver.Resolve(
            builder.Configuration["DocsIngestorMcp:VectorDatabasePath"],
            AppContext.BaseDirectory, "data/gnougo-docs-ingestor-vectors.sqlite");
        var originalsDirectory = DocsIngestorMcpPathResolver.Resolve(
            builder.Configuration["DocsIngestorMcp:OriginalsDirectory"],
            AppContext.BaseDirectory, "data/docs-ingestor/originals");
        var keyVaultDbPath = DocsIngestorMcpPathResolver.Resolve(
            builder.Configuration["KeyVault:DatabasePath"],
            AppContext.BaseDirectory, "data/gnougo-keyvault.db");

        Directory.CreateDirectory(Path.GetDirectoryName(metadataDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(vectorDbPath)!);
        Directory.CreateDirectory(originalsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(keyVaultDbPath)!);

        builder.Services.AddDocsIngestorCoreServices(metadataDbPath, vectorDbPath, originalsDirectory, keyVaultDbPath);
        builder.Services.AddDocsIngestorMcpHttpServer();

        // Register source-generated JSON context for AOT-safe minimal-API serialization.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, DocsIngestorJsonContext.Default));

        var app = builder.Build();
        app.Services.InitializeDocsIngestorMcpAsync().GetAwaiter().GetResult();

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
            metadataDbPath,
            vectorDbPath,
            originalsDirectory,
            keyVaultDbPath,
            routePrefix);

        return app;
    }

    private static void BindOptions(IConfiguration configuration, DocsIngestorMcpOptions options)
    {
        var section = configuration.GetSection("DocsIngestorMcp");
        options.DatabasePath = ReadString(section, "DatabasePath", options.DatabasePath);
        options.VectorDatabasePath = ReadString(section, "VectorDatabasePath", options.VectorDatabasePath);
        options.OriginalsDirectory = ReadString(section, "OriginalsDirectory", options.OriginalsDirectory);
        options.DefaultCollection = ReadString(section, "DefaultCollection", options.DefaultCollection);
        options.DefaultEmbeddingConfigName = ReadString(section, "DefaultEmbeddingConfigName", options.DefaultEmbeddingConfigName);
        options.DefaultTenantId = ReadString(section, "DefaultTenantId", options.DefaultTenantId);
        options.DefaultAuthor = ReadString(section, "DefaultAuthor", options.DefaultAuthor);
        options.DownloadTimeoutSeconds = ReadInt(section, "DownloadTimeoutSeconds", options.DownloadTimeoutSeconds);
        options.MaxDownloadBytes = ReadLong(section, "MaxDownloadBytes", options.MaxDownloadBytes);

        var chunking = section.GetSection("Chunking");
        options.Chunking.Mode = ReadString(chunking, "Mode", options.Chunking.Mode);
        options.Chunking.MinTokens = ReadInt(chunking, "MinTokens", options.Chunking.MinTokens);
        options.Chunking.TargetTokens = ReadInt(chunking, "TargetTokens", options.Chunking.TargetTokens);
        options.Chunking.MaxTokens = ReadInt(chunking, "MaxTokens", options.Chunking.MaxTokens);
        options.Chunking.OverlapTokens = ReadInt(chunking, "OverlapTokens", options.Chunking.OverlapTokens);

        var images = section.GetSection("Images");
        options.Images.EnableImageDiscovery = ReadBool(images, "EnableImageDiscovery", options.Images.EnableImageDiscovery);
        options.Images.LoadImageBytes = ReadBool(images, "LoadImageBytes", options.Images.LoadImageBytes);
        options.Images.EnableOcr = ReadBool(images, "EnableOcr", options.Images.EnableOcr);
        options.Images.OcrLanguage = ReadString(images, "OcrLanguage", options.Images.OcrLanguage);
        options.Images.OcrDpi = ReadInt(images, "OcrDpi", options.Images.OcrDpi);
    }

    private static string ReadString(IConfiguration section, string name, string defaultValue)
        => string.IsNullOrWhiteSpace(section[name]) ? defaultValue : section[name]!;

    private static int ReadInt(IConfiguration section, string name, int defaultValue)
        => int.TryParse(section[name], out var value) ? value : defaultValue;

    private static long ReadLong(IConfiguration section, string name, long defaultValue)
        => long.TryParse(section[name], out var value) ? value : defaultValue;

    private static bool ReadBool(IConfiguration section, string name, bool defaultValue)
        => bool.TryParse(section[name], out var value) ? value : defaultValue;
}
