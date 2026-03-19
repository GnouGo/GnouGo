﻿﻿using DocIngestor.Cli.Configuration;
using DocIngestor.Cli.Debugging;
using DocIngestor.Cli.DependencyInjection;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Pipeline;
using DocIngestor.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DocIngestor.Cli.Commands;

/// <summary>
/// Commande d'ingestion de documents.
/// </summary>
public static class IngestCommand
{
    public static async Task<int> RunAsync(string[] args, AppSettings config)
    {
        var path = CommandLineParser.GetArg(args, "--path") ?? CommandLineParser.GetArg(args, "--input");
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("Missing --path");
            HelpCommand.PrintUsage();
            return 2;
        }

        // Configurer le ServiceProvider avec injection de dépendances
        var services = new ServiceCollection();
        services.ConfigureServices(config, args);
        
        using var serviceProvider = services.BuildServiceProvider();

        // Parsing des options
        var options = ParseIngestionOptions(args, config);
        
        // OpenTelemetry
        var (tracerProvider, meterProvider, telemetry) = ConfigureOpenTelemetryProviders(args, config);

        try
        {
            Directory.CreateDirectory(options.Store.StoreName == "sqlite" 
                ? Path.GetDirectoryName(Path.Combine(config.DocIngestor.Store.StoreDirectory, "vectors.sqlite"))! 
                : config.DocIngestor.Store.StoreDirectory);

            // Récupérer le pipeline depuis le ServiceProvider
            var pipeline = serviceProvider.GetRequiredService<DocumentIngestionPipeline>();
            var fileProvider = serviceProvider.GetRequiredService<IFileProvider>();

            // Collect files via IFileProvider
            var sources = new List<DocumentSource>();
            await foreach (var src in fileProvider.EnumerateAsync(path))
            {
                sources.Add(src);
            }

            if (sources.Count == 0)
            {
                Console.WriteLine($"Path not found or no supported files: {path}");
                return 2;
            }

            // Process files
            var (ok, failed, skipped) = await ProcessFiles(sources, pipeline, options, config);

            Console.WriteLine($"Done. ok={ok} skipped={skipped} failed={failed}");
            return failed == 0 ? 0 : 1;
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

    private static IngestionOptions ParseIngestionOptions(string[] args, AppSettings config)
    {
        // Chunking options
        var modeStr = (CommandLineParser.GetArg(args, "--mode") ?? config.DocIngestor.Chunking.Mode).Trim().ToLowerInvariant();
        var mode = modeStr switch
        {
            "semantic" => ChunkingMode.Semantic,
            "recursive" => ChunkingMode.Recursive,
            "auto" => ChunkingMode.Auto,
            _ => ChunkingMode.Auto,
        };
        var minTokens = CommandLineParser.GetInt(args, "--minTokens", config.DocIngestor.Chunking.MinTokens);
        var targetTokens = CommandLineParser.GetInt(args, "--targetTokens", config.DocIngestor.Chunking.TargetTokens);
        var maxTokens = CommandLineParser.GetInt(args, "--maxTokens", config.DocIngestor.Chunking.MaxTokens);
        var overlapTokens = CommandLineParser.GetInt(args, "--overlapTokens", config.DocIngestor.Chunking.OverlapTokens);

        // Embedding options
        var embed = CommandLineParser.GetBool(args, "--embed", config.DocIngestor.Embedding.Enabled);
        var modelName = CommandLineParser.GetArg(args, "--model") ?? config.DocIngestor.Embedding.DefaultModel;

        // Store options
        var storeEnabled = CommandLineParser.GetBool(args, "--store", config.DocIngestor.Store.Enabled);
        var storeName = CommandLineParser.GetArg(args, "--storeName") ?? config.DocIngestor.Store.DefaultStore;
        var collection = CommandLineParser.GetArg(args, "--collection") ?? config.DocIngestor.Store.DefaultCollection;

        // Images options
        var images = CommandLineParser.GetBool(args, "--images", config.DocIngestor.Images.Enabled);
        var loadImageBytes = CommandLineParser.GetBool(args, "--loadImageBytes", config.DocIngestor.Images.LoadBytes);
        var ocr = CommandLineParser.GetBool(args, "--ocr", config.DocIngestor.Images.Ocr.Enabled);
        var ocrLang = CommandLineParser.GetArg(args, "--ocr-lang") ?? config.DocIngestor.Images.Ocr.Language;
        var ocrDpi = CommandLineParser.GetInt(args, "--ocr-dpi", config.DocIngestor.Images.Ocr.Dpi);

        // Debug
        var debug = CommandLineParser.GetBool(args, "--debug", config.DocIngestor.Debug.Enabled);
        if (debug)
        {
            images = true;
            loadImageBytes = true;
        }

        return new IngestionOptions(
            ChunkingMode: mode,
            ChunkPolicy: new ChunkSizePolicy(minTokens, targetTokens, maxTokens, overlapTokens),
            EmbeddingModelName: modelName,
            SemanticSimilarityThreshold: CommandLineParser.GetDouble(args, "--semanticThreshold", 0.80),
            EnableEmbedding: embed,
            Images: new ImageExtractionOptions(
                EnableImageDiscovery: images,
                LoadImageBytes: loadImageBytes,
                EnableOcr: ocr,
                OcrLanguage: ocrLang,
                OcrDpi: ocrDpi
            ),
            Store: new StoreOptions(
                EnableStore: storeEnabled,
                StoreName: storeName,
                Collection: collection
            )
        );
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

    private static async Task<(int ok, int failed, int skipped)> ProcessFiles(
        List<DocumentSource> sources,
        DocumentIngestionPipeline pipeline,
        IngestionOptions options,
        AppSettings config)
    {
        int ok = 0, failed = 0, skipped = 0;
        var debug = config.DocIngestor.Debug.Enabled;
        var debugDir = config.DocIngestor.Debug.OutputDirectory;

        if (debug)
        {
            Directory.CreateDirectory(debugDir);
        }

        foreach (var source in sources)
        {
            try
            {
                await using (source)
                {
                    var (_, chunks, imagesOut, embedded) = await pipeline.RunAsync(source, options);
                    ok++;
                
                    if (debug)
                        await DebugArtifactWriter.WriteAsync(source.FileName, chunks, imagesOut, debugDir);
                
                    Console.WriteLine($"[OK] {source.FileName}  chunks={chunks.Count} images={imagesOut.Count} embedded={embedded.Count}");
                }
            }
            catch (NotSupportedException)
            {
                skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"[ERROR] {source.FileName}: {ex.Message}");
            }
        }

        return (ok, failed, skipped);
    }
}

