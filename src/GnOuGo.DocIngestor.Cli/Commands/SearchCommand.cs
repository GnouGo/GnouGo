﻿using DocIngestor.Cli.Configuration;
using DocIngestor.Cli.DependencyInjection;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Stores;
using DocIngestor.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DocIngestor.Cli.Commands;

/// <summary>
/// Commande de recherche dans le store vectoriel.
/// </summary>
public static class SearchCommand
{
    public static async Task<int> RunAsync(string[] args, AppSettings config)
    {
        var query = CommandLineParser.GetArg(args, "--query");
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.WriteLine("Missing --query");
            HelpCommand.PrintUsage();
            return 2;
        }

        // Store options
        var storeName = CommandLineParser.GetArg(args, "--storeName") ?? config.DocIngestor.Store.DefaultStore;
        var collection = CommandLineParser.GetArg(args, "--collection") ?? config.DocIngestor.Store.DefaultCollection;
        var topK = CommandLineParser.GetInt(args, "--topK", config.DocIngestor.Search.DefaultTopK);
        var modelName = CommandLineParser.GetArg(args, "--model") ?? config.DocIngestor.Embedding.DefaultModel;

        // Reranker options
        var rerankEnabled = CommandLineParser.GetBool(args, "--rerank", config.DocIngestor.Search.Reranker.Enabled);
        var rerankerType = CommandLineParser.GetArg(args, "--reranker") ?? config.DocIngestor.Search.Reranker.DefaultType;
        var vectorWeight = CommandLineParser.GetDouble(args, "--vector-weight", config.DocIngestor.Search.Reranker.VectorWeight);
        var rerankWeight = CommandLineParser.GetDouble(args, "--rerank-weight", config.DocIngestor.Search.Reranker.RerankWeight);

        // Configurer le ServiceProvider
        var services = new ServiceCollection();
        services.ConfigureServices(config, args);
        
        using var serviceProvider = services.BuildServiceProvider();

        // OpenTelemetry
        var (tracerProvider, meterProvider, telemetry) = ConfigureOpenTelemetryProviders(args, config);

        try
        {
            Directory.CreateDirectory(config.DocIngestor.Store.StoreDirectory);

            // Récupérer les services depuis le ServiceProvider
            var storeRouter = serviceProvider.GetRequiredService<IVectorStoreRouter>();
            var embeddingRouter = serviceProvider.GetRequiredService<IEmbeddingRouter>();
            var rerankerRouter = serviceProvider.GetRequiredService<IRerankerRouter>();

            var store = storeRouter.Get(storeName);
            if (store is not IVectorSearchStore searchStore)
            {
                Console.WriteLine($"Store '{storeName}' does not support search.");
                return 2;
            }

            var model = embeddingRouter.Get(modelName);
            var qvec = await model.EmbedAsync(query);

            // Fetch more candidates if reranking is enabled
            var fetchK = rerankEnabled ? Math.Max(topK * 3, 30) : topK;
            var results = await searchStore.SearchAsync(collection, qvec, fetchK);

            // Apply reranker if enabled
            if (rerankEnabled && results.Count > 0)
            {
                var reranker = rerankerRouter.Get(rerankerType);
                var opts = new RerankerOptions(
                    TopK: topK,
                    VectorWeight: vectorWeight,
                    RerankWeight: rerankWeight);

                Console.WriteLine($"[Reranker] Applying '{rerankerType}' on {results.Count} candidates (vectorWeight={vectorWeight}, rerankWeight={rerankWeight})...");
                results = await reranker.RerankAsync(query, results, opts);
                Console.WriteLine($"[Reranker] Done — {results.Count} results after reranking.");
            }
            else if (results.Count > topK)
            {
                results = results.Take(topK).ToList();
            }

            foreach (var r in results)
            {
                Console.WriteLine("------------------------------------------------------------");
                Console.WriteLine($"score={r.Score:F4}  doc={r.Chunk.Chunk.DocumentId}  section={r.Chunk.Chunk.SectionId}  idx={r.Chunk.Chunk.Index}");
                if (r.Chunk.Chunk.Metadata.TryGetValue("page", out var page))
                    Console.WriteLine($"page={page}");
                Console.WriteLine();
                Console.WriteLine(r.Chunk.Chunk.Text);
            }

            return 0;
        }
        finally
        {
            if (tracerProvider != null || meterProvider != null)
            {
                OpenTelemetryConfiguration.Shutdown(tracerProvider, meterProvider);
                telemetry?.Dispose();
            }
        }
    }

    private static (TracerProvider?, MeterProvider?, GenAiTelemetry?) ConfigureOpenTelemetryProviders(
        string[] args,
        AppSettings config)
    {
        var enableOtel = CommandLineParser.GetBool(args, "--enable-otel", config.OpenTelemetry.Enabled);
        
        if (!enableOtel)
            return (null, null, null);

        var otlpEndpoint = CommandLineParser.GetArg(args, "--otlp-endpoint") ?? config.OpenTelemetry.OtlpEndpoint;
        var tenantId = CommandLineParser.GetArg(args, "--tenant-id") ?? config.OpenTelemetry.TenantId;

        var (tracerProvider, meterProvider) = OpenTelemetryConfiguration.ConfigureOpenTelemetry(
            serviceName: config.OpenTelemetry.ServiceName,
            otlpEndpoint: otlpEndpoint,
            tenantId: tenantId
        );
        
        var telemetry = new GenAiTelemetry();
        Console.WriteLine("[OpenTelemetry] GenAI instrumentation enabled.");
        
        return (tracerProvider, meterProvider, telemetry);
    }
}

