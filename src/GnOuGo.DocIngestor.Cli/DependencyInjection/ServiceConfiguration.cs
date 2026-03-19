﻿﻿﻿﻿using DocIngestor.Cli.Commands;
using DocIngestor.Cli.Configuration;
using DocIngestor.Cli.IO;
using DocIngestor.Core.Abstractions;
using GnOuGo.Auth.Core;
using DocIngestor.Core.Embeddings;
using DocIngestor.Core.Extractors;
using DocIngestor.Core.Images;
using DocIngestor.Core.Ocr;
using DocIngestor.Core.Pipeline;
using DocIngestor.Core.Reranking;
using DocIngestor.Core.Stores;
using DocIngestor.Core.Telemetry;
using DocIngestor.Core.Tokenization;
using Microsoft.Extensions.DependencyInjection;

namespace DocIngestor.Cli.DependencyInjection;

/// <summary>
/// Configuration des services pour l'injection de dépendances.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Configure tous les services du moteur d'ingestion.
    /// </summary>
    public static IServiceCollection ConfigureServices(
        this IServiceCollection services,
        AppSettings config,
        string[] args)
    {
        // Configuration
        services.AddSingleton(config);

        // OpenAI config (from appsettings.json "OpenAi" section)
        var openAiConfig = config.OpenAi;
        services.AddSingleton(openAiConfig);

        // HttpClient
        services.AddHttpClient();

        // Tokenization
        services.AddSingleton<ITokenCounter, DefaultTokenCounter>();

        // OCR Engine (optionnel)
        var ocr = CommandLineParser.GetBool(args, "--ocr", config.DocIngestor.Images.Ocr.Enabled);
        if (ocr)
        {
            var ocrEngine = CommandLineParser.GetArg(args, "--ocr-engine")
                            ?? config.DocIngestor.Images.Ocr.Engine;

            ConfigureOcrEngine(services, ocrEngine, config);
        }
        else
        {
            services.AddSingleton<IOcrEngine>(_ => null!);
        }

        // OpenTelemetry
        ConfigureOpenTelemetry(services, config, args);

        // OIDC Provider (si configuré)
        ConfigureOidcProvider(services, args, openAiConfig);

        // Embedding Models
        ConfigureEmbeddingModels(services, args, openAiConfig, config.Ollama);

        // Vector Stores
        ConfigureVectorStores(services, config);

        // Rerankers
        ConfigureRerankers(services, args, openAiConfig, config.Ollama);

        // Document Extractors
        ConfigureDocumentExtractors(services);

        // Image Extractors
        ConfigureImageExtractors(services);

        // Document Router
        services.AddSingleton<DocumentRouter>();

        // File Provider (disk-based for CLI)
        services.AddSingleton<IFileProvider, DiskFileProvider>();

        // Pipeline
        services.AddSingleton<DocumentIngestionPipeline>();

        return services;
    }

    private static void ConfigureOpenTelemetry(
        IServiceCollection services,
        AppSettings config,
        string[] args)
    {
        var enableOtel = CommandLineParser.GetBool(args, "--enable-otel", config.OpenTelemetry.Enabled);
        
        if (enableOtel)
        {
            services.AddSingleton<GenAiTelemetry>();
        }
        else
        {
            services.AddSingleton<GenAiTelemetry>(_ => null!);
        }
    }

    private static void ConfigureOidcProvider(IServiceCollection services, string[] args, OpenAiConfig openAiConfig)
    {
        // Priorité 1 : API Key en ligne de commande
        var apiKey = CommandLineParser.GetArg(args, "--api-key");
        
        // Priorité 2 : API Key depuis appsettings.json
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = openAiConfig.ApiKey;
        }
        
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Authentification simple avec API Key
            services.AddSingleton<IApiKeyProvider>(new StaticApiKeyProvider(apiKey));
            return;
        }

        // Sinon, vérifier la configuration OIDC (CLI puis appsettings.json)
        var oidcIssuer = CommandLineParser.GetArg(args, "--oidc-issuer") ?? openAiConfig.Issuer;
        var oidcClientId = CommandLineParser.GetArg(args, "--oidc-client-id") ?? openAiConfig.ClientId;
        var oidcScopes = CommandLineParser.GetArg(args, "--oidc-scopes") ?? openAiConfig.Scopes;
        var oidcClientSecret = CommandLineParser.GetArg(args, "--oidc-client-secret") ?? openAiConfig.ClientSecret;
        var oidcPrivateKeyPem = CommandLineParser.GetPemFromArgs(args, "--oidc-private-key-path", "--oidc-private-key");

        if (!string.IsNullOrWhiteSpace(oidcIssuer) &&
            !string.IsNullOrWhiteSpace(oidcClientId) &&
            !string.IsNullOrWhiteSpace(oidcScopes) &&
            (!string.IsNullOrWhiteSpace(oidcClientSecret) || !string.IsNullOrWhiteSpace(oidcPrivateKeyPem)))
        {
            services.AddSingleton<IApiKeyProvider>(sp =>
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                return new OidcJwtApiKeyProvider(httpClient,
                    new OidcClientCredentialsConfig(
                        Issuer: oidcIssuer,
                        ClientId: oidcClientId,
                        Scopes: oidcScopes,
                        ClientSecret: oidcClientSecret,
                        PrivateKeyPem: oidcPrivateKeyPem
                    ));
            });
        }
        else
        {
            services.AddSingleton<IApiKeyProvider>(_ => null!);
        }
    }

    private static void ConfigureEmbeddingModels(IServiceCollection services, string[] args, OpenAiConfig openAiConfig, OllamaConfig ollamaConfig)
    {
        // Priorité : CLI > appsettings.json (qui a un défaut vers OpenAI officiel)
        var endpointUrl = CommandLineParser.GetArg(args, "--endpoint-url") 
                          ?? openAiConfig.EndpointUrl;

        services.AddSingleton<IEmbeddingRouter>(sp =>
        {
            var models = new List<IEmbeddingModel>
            {
                new HashEmbeddingModel("hash-384", 384),
                new HashEmbeddingModel("hash-768", 768)
            };

            // Ajouter ada3-large si API Key ou OIDC configuré
            var apiKeyProvider = sp.GetService<IApiKeyProvider>();
            if (apiKeyProvider != null)
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                var telemetry = sp.GetService<GenAiTelemetry>();
                
                models.Add(new OpenAiCompatibleEmbeddingModel(
                    name: "ada3-large",
                    endpointUrl: endpointUrl,
                    model: "text-embedding-3-large",
                    apiKeyProvider: apiKeyProvider,
                    http: httpClient,
                    defaultDims: 3072,
                    telemetry: telemetry
                ));
            }

            // Ollama local embedding model
            if (ollamaConfig.Enabled)
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                models.Add(new OllamaEmbeddingModel(
                    name: $"ollama-{ollamaConfig.EmbeddingModel}",
                    baseUrl: ollamaConfig.BaseUrl,
                    model: ollamaConfig.EmbeddingModel,
                    http: httpClient,
                    defaultDims: ollamaConfig.DefaultDimensions));
            }

            return new EmbeddingRegistry(models);
        });
    }

    private static void ConfigureVectorStores(IServiceCollection services, AppSettings config)
    {
        services.AddSingleton<IVectorStoreRouter>(sp =>
        {
            var storeDir = config.DocIngestor.Store.StoreDirectory;
            var sqlitePath = Path.Combine(storeDir, "vectors.sqlite");

            return new VectorStoreRegistry(new IVectorStore[]
            {
                new JsonlVectorStore(storeDir),
                new SqliteCosineVectorStore(sqlitePath)
            });
        });
    }

    private static void ConfigureRerankers(IServiceCollection services, string[] args, OpenAiConfig openAiConfig, OllamaConfig ollamaConfig)
    {
        var endpointUrl = CommandLineParser.GetArg(args, "--endpoint-url")
                          ?? openAiConfig.EndpointUrl;

        services.AddSingleton<IRerankerRouter>(sp =>
        {
            var rerankers = new List<IReranker> { new Bm25Reranker() };

            var telemetry = sp.GetService<GenAiTelemetry>();

            // OpenAI cross-encoder if API key is available
            var apiKeyProvider = sp.GetService<IApiKeyProvider>();
            if (apiKeyProvider is not null)
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                var scorer = new OpenAiChatScorer(
                    endpointUrl: endpointUrl,
                    model: "gpt-4o-mini",
                    apiKeyProvider: apiKeyProvider,
                    http: httpClient,
                    telemetry: telemetry);
                rerankers.Add(new CrossEncoderReranker(scorer, maxConcurrency: 5));
            }

            // Ollama cross-encoder if Ollama is enabled
            if (ollamaConfig.Enabled)
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                var scorer = new OllamaChatScorer(
                    baseUrl: ollamaConfig.BaseUrl,
                    model: ollamaConfig.ChatModel,
                    http: httpClient,
                    telemetry: telemetry);
                rerankers.Add(new CrossEncoderReranker(scorer, maxConcurrency: 3));
            }

            return new RerankerRegistry(rerankers);
        });
    }

    private static void ConfigureDocumentExtractors(IServiceCollection services)
    {
        // Binary-format extractors (priority — registered first)
        services.AddSingleton<IDocumentTextExtractor, PdfPigExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxOpenXmlExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PptxOpenXmlExtractor>();
        services.AddSingleton<IDocumentTextExtractor, XlsxOpenXmlExtractor>();

        // Universal plain-text catch-all (code, config, markdown, JSON, YAML, etc.)
        // Must be last — the router picks the first extractor that CanHandle.
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
    }

    private static void ConfigureImageExtractors(IServiceCollection services)
    {
        services.AddSingleton<IImageExtractor, DocxImageExtractor>();
        services.AddSingleton<IImageExtractor, PptxImageExtractor>();
        services.AddSingleton<IImageExtractor, PdfPigImageExtractor>();
        services.AddSingleton<IImageExtractor, XlsxImageExtractor>();
    }

    private static void ConfigureOcrEngine(IServiceCollection services, string engine, AppSettings config)
    {
        switch (engine.ToLowerInvariant())
        {
            case "ollama":
                services.AddSingleton<IOcrEngine>(sp =>
                {
                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                    var telemetry = sp.GetService<GenAiTelemetry>();
                    return new OllamaVisionOcrEngine(
                        baseUrl: config.Ollama.BaseUrl,
                        model: config.Ollama.VisionModel,
                        http: httpClient,
                        telemetry: telemetry);
                });
                break;

            case "openai":
            default:
                services.AddSingleton<IOcrEngine>(sp =>
                {
                    var apiKeyProvider = sp.GetService<IApiKeyProvider>();
                    if (apiKeyProvider is null)
                        throw new InvalidOperationException(
                            "OpenAI Vision OCR requires an API key. Configure OpenAi:ApiKey or use --ocr-engine ollama.");

                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                    var telemetry = sp.GetService<GenAiTelemetry>();
                    return new OpenAiVisionOcrEngine(
                        endpointUrl: config.OpenAi.EndpointUrl,
                        model: "gpt-4o-mini",
                        apiKeyProvider: apiKeyProvider,
                        http: httpClient,
                        telemetry: telemetry);
                });
                break;
        }
    }
}
