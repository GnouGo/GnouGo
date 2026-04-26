using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Extractors;
using DocIngestor.Core.Images;
using DocIngestor.Core.Ocr;
using DocIngestor.Core.Pipeline;
using DocIngestor.Core.Stores;
using DocIngestor.Core.Tokenization;
using GnOuGo.DocIngestor.Mcp.Data;
using GnOuGo.DocIngestor.Mcp.Services;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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

        services.AddDbContext<KeyVaultDbContext>(options => options.UseSqlite($"Data Source={keyVaultDatabasePath}"));
        services.AddScoped<KeyVaultService>();
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

        services.AddTransient<DocsIngestorTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = ServerVersion
                };
            })
            .WithHttpTransport()
            .WithTools<DocsIngestorTools>();

        return services;
    }

    public static async Task InitializeDocsIngestorMcpAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<StoredDocumentRepository>();
        await repository.InitializeAsync(ct);

        var keyVaultDb = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
        await GnOuGo.KeyVault.Core.Data.KeyVaultDatabaseBootstrap.EnsureCreatedAsync(keyVaultDb, ct);
        var keyVault = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
        await keyVault.EnsureDefaultKeyPairAsync(ct);
    }

    public static IEndpointConventionBuilder MapDocsIngestorMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern).DisableAntiforgery();
    }
}

