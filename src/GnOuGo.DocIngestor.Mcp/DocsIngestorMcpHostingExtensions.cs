using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Extractors;
using DocIngestor.Core.Images;
using DocIngestor.Core.Ocr;
using DocIngestor.Core.Pipeline;
using DocIngestor.Core.Stores;
using DocIngestor.Core.Tokenization;
using GnOuGo.DocIngestor.Mcp.Data;
using GnOuGo.DocIngestor.Mcp.Models;
using GnOuGo.DocIngestor.Mcp.Services;
using GnOuGo.Mcp.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace GnOuGo.DocIngestor.Mcp;

public static class DocsIngestorMcpHostingExtensions
{
    public const string ServerName = "GnOuGo.DocIngestor.Mcp";
    public const string ServerVersion = "1.0.0";
    public const string DefaultRoutePrefix = "/mcp";

    public static IServiceCollection AddDocsIngestorMcpServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        services.Configure<DocsIngestorMcpOptions>(options => BindOptions(configuration, options));

        var paths = new DocsIngestorMcpStoragePaths(
            DocsIngestorMcpPathResolver.Resolve(
                configuration["DocsIngestorMcp:DatabasePath"],
                baseDirectory,
                ".GnOuGo/data/gnougo-docs-ingestor-mcp.db"),
            DocsIngestorMcpPathResolver.Resolve(
                configuration["DocsIngestorMcp:VectorDatabasePath"],
                baseDirectory,
                ".GnOuGo/data/gnougo-docs-ingestor-vectors.sqlite"),
            DocsIngestorMcpPathResolver.Resolve(
                configuration["DocsIngestorMcp:OriginalsDirectory"],
                baseDirectory,
                ".GnOuGo/data/docs-ingestor/originals"),
            DocsIngestorMcpPathResolver.Resolve(
                configuration["KeyVault:DatabasePath"],
                baseDirectory,
                ".GnOuGo/data/gnougo-keyvault.db"));

        Directory.CreateDirectory(Path.GetDirectoryName(paths.MetadataDatabasePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.VectorDatabasePath)!);
        Directory.CreateDirectory(paths.OriginalsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.KeyVaultDatabasePath)!);

        services.AddSingleton(paths);
        services.AddDocsIngestorCoreServices(
            paths.MetadataDatabasePath,
            paths.VectorDatabasePath,
            paths.OriginalsDirectory,
            paths.KeyVaultDatabasePath);

        return services;
    }

    public static IServiceCollection AddDocsIngestorCoreServices(
        this IServiceCollection services,
        string metadataDatabasePath,
        string vectorDatabasePath,
        string originalsDirectory,
        string keyVaultDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient();
        services.AddOptions<DocsIngestorMcpOptions>();

        services.AddSingleton(new KeyVaultSecretStore(keyVaultDatabasePath));
        services.AddScoped<KeyVaultEmbeddingConfigProvider>();

        services.AddSingleton(new StoredDocumentRepository(metadataDatabasePath));
        services.AddSingleton(new OriginalDocumentStore(originalsDirectory));
        services.AddScoped<UrlDownloadService>();
        services.AddScoped<DocsIngestorMcpService>();

        services.AddSingleton<ITokenCounter, DefaultTokenCounter>();
        services.AddSingleton<IDocumentTextExtractor, PdfPigExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxOpenXmlExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PptxOpenXmlExtractor>();
        services.AddSingleton<IDocumentTextExtractor, XlsxOpenXmlExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        services.AddSingleton<IImageExtractor, DocxImageExtractor>();
        services.AddSingleton<IImageExtractor, PptxImageExtractor>();
        services.AddSingleton<IImageExtractor, PdfPigImageExtractor>();
        services.AddSingleton<IImageExtractor, XlsxImageExtractor>();
        services.AddSingleton<DocumentRouter>();
        services.AddSingleton<IEmbeddingRouter, DefaultEmbeddingRouter>();
        services.AddSingleton<IOcrEngine, FakeOcrEngine>();
        services.AddScoped<DocumentIngestionPipeline>();

        services.AddSingleton<SqliteCosineVectorStore>(_ => new SqliteCosineVectorStore(vectorDatabasePath));
        services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<SqliteCosineVectorStore>());
        services.AddSingleton<IVectorSearchStore>(sp => sp.GetRequiredService<SqliteCosineVectorStore>());
        services.AddSingleton<IVectorStoreAdmin>(sp => sp.GetRequiredService<SqliteCosineVectorStore>());
        services.AddSingleton<IVectorStoreRouter>(sp => new VectorStoreRegistry(new IVectorStore[] { sp.GetRequiredService<SqliteCosineVectorStore>() }));

        return services;
    }

    public static IServiceCollection AddDocsIngestorMcpHttpServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = ServerVersion
                };
                options.AddGnOuGoToolErrorNormalizer();
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithDocsIngestorMcpTools();

        return services;
    }

    public static IMcpServerBuilder WithDocsIngestorMcpTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddTransient<DocsIngestorTools>();
        return builder.WithTools<DocsIngestorTools>(DocsIngestorMcpJson.SerializerOptions);
    }

    public static async Task InitializeDocsIngestorMcpAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<StoredDocumentRepository>();
        await repository.InitializeAsync(ct);

        var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultSecretStore>();
        await keyVault.InitializeAsync(ct);
    }

    public static IEndpointConventionBuilder MapDocsIngestorMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern).DisableAntiforgery();
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

internal sealed record DocsIngestorMcpStoragePaths(
    string MetadataDatabasePath,
    string VectorDatabasePath,
    string OriginalsDirectory,
    string KeyVaultDatabasePath);
