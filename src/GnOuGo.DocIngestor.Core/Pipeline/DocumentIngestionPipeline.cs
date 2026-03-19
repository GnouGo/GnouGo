using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Chunking;
using DocIngestor.Core.Models;
using DocIngestor.Core.Formatting;
using DocIngestor.Core.Stores;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Pipeline;

/// <summary>
/// Resolved service instances for a single pipeline run (produced by the routing step).
/// </summary>
internal readonly record struct ResolvedPipelineServices(
    IDocumentTextExtractor TextExtractor,
    IImageExtractor? ImageExtractor,
    IEmbeddingModel? EmbeddingModel,
    IVectorStore? VectorStore);

public sealed class DocumentIngestionPipeline
{
    private readonly DocumentRouter _router;
    private readonly ITokenCounter _tokenCounter;
    private readonly IEmbeddingRouter _embeddingRouter;
    private readonly IVectorStoreRouter _storeRouter;
    private readonly IOcrEngine? _ocrEngine;
    private readonly GenAiTelemetry? _telemetry;

    public DocumentIngestionPipeline(
        DocumentRouter router,
        ITokenCounter tokenCounter,
        IEmbeddingRouter embeddingRouter,
        IVectorStoreRouter storeRouter,
        IOcrEngine? ocrEngine = null,
        GenAiTelemetry? telemetry = null)
    {
        _router = router;
        _tokenCounter = tokenCounter;
        _embeddingRouter = embeddingRouter;
        _storeRouter = storeRouter;
        _ocrEngine = ocrEngine;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Public entry point: resolves concrete implementations via routers, then delegates to the pure pipeline.
    /// </summary>
    public async ValueTask<(ExtractedDocument Doc, IReadOnlyList<TextChunk> Chunks, IReadOnlyList<ImageArtifact> Images, IReadOnlyList<EmbeddedChunk> Embedded)>
        RunAsync(DocumentSource source, IngestionOptions options, CancellationToken ct = default)
    {
        source = await DocumentSource.EnsureSeekableAsync(source, ct);

        // ── Resolve concrete services via routers ───────────────────────
        var textExtractor = _router.GetTextExtractor(source.FileName, source.ContentType);

        var imageExtractor = options.Images.EnableImageDiscovery
            ? _router.TryGetImageExtractor(source.FileName, source.ContentType)
            : null;


        IEmbeddingModel? embeddingModel = options.EnableEmbedding
            ? _embeddingRouter.Get(options.EmbeddingModelName)
            : null;

        var storeOptions = options.Store ?? new StoreOptions();
        IVectorStore? vectorStore = storeOptions.EnableStore
            ? _storeRouter.Get(storeOptions.StoreName)
            : null;

        var services = new ResolvedPipelineServices(
            TextExtractor: textExtractor,
            ImageExtractor: imageExtractor,
            EmbeddingModel: embeddingModel,
            VectorStore: vectorStore);

        return await ExecutePipelineAsync(source, options, services, ct);
    }

    /// <summary>
    /// Pure pipeline execution: all concrete implementations are injected — no router calls.
    /// </summary>
    private async ValueTask<(ExtractedDocument Doc, IReadOnlyList<TextChunk> Chunks, IReadOnlyList<ImageArtifact> Images, IReadOnlyList<EmbeddedChunk> Embedded)>
        ExecutePipelineAsync(DocumentSource source, IngestionOptions options, ResolvedPipelineServices services, CancellationToken ct)
    {
        using var pipelineActivity = GenAiTelemetry.GetActivitySource().StartActivity("document.ingestion.pipeline", ActivityKind.Internal);
        pipelineActivity?.SetTag("document.name", source.FileName);
        pipelineActivity?.SetTag("chunking.mode", options.ChunkingMode.ToString());
        pipelineActivity?.SetTag("embedding.model", options.EmbeddingModelName);
        pipelineActivity?.SetTag("embedding.enabled", options.EnableEmbedding);

        var startTime = Stopwatch.GetTimestamp();

        try
        {
            // ---- 1) Extract base text
            ExtractedDocument doc;
            using (var extractActivity = GenAiTelemetry.GetActivitySource().StartActivity("document.extraction", ActivityKind.Internal))
            {
                extractActivity?.SetTag("document.type", Path.GetExtension(source.FileName));
                extractActivity?.SetTag("extractor.type", services.TextExtractor.GetType().Name);

                doc = await services.TextExtractor.ExtractAsync(source, ct);

                extractActivity?.SetTag("document.sections.count", doc.Sections.Count);
                extractActivity?.SetTag("document.text.length", doc.Sections.Sum(s => s.Text.Length));
            }

            if (options.Images.EnableOcr && !options.Images.EnableImageDiscovery)
                throw new InvalidOperationException("OCR requires Images.EnableImageDiscovery = true.");

            // ---- 2) Extract images (optional)
            IReadOnlyList<ImageArtifact> images = Array.Empty<ImageArtifact>();
            if (options.Images.EnableImageDiscovery && services.ImageExtractor is not null)
            {
                using var imageActivity = GenAiTelemetry.GetActivitySource().StartActivity("image.extraction", ActivityKind.Internal);
                imageActivity?.SetTag("image.discovery.enabled", true);
                imageActivity?.SetTag("image.load_bytes", options.Images.LoadImageBytes);
                imageActivity?.SetTag("image.extractor.type", services.ImageExtractor.GetType().Name);

                images = await services.ImageExtractor.ExtractImagesAsync(source,
                    options.Images with { LoadImageBytes = options.Images.LoadImageBytes || options.Images.EnableOcr }, ct);

                imageActivity?.SetTag("image.count", images.Count);
                imageActivity?.SetTag("image.total_size_bytes", images.Sum(i => i.LengthBytes ?? 0));
            }

            // ---- 3) OCR (optional): replace PDF image placeholders with OCR text
            if (options.Images.EnableOcr)
            {
                (doc, images) = await ApplyOcrAsync(doc, images, options, ct);
            }
            else
            {
                // Strip placeholders so they don't pollute chunking/embeddings.
                var newSections = doc.Sections.Select(s =>
                    s with { Text = Regex.Replace(s.Text, @"\[\[PDF_IMAGE[^\]]*\]\]", "", RegexOptions.CultureInvariant) })
                    .ToList();
                doc = doc with { Sections = newSections };
            }

            // ---- 4) Chunking
            IReadOnlyList<TextChunk> chunks;
            using (var chunkActivity = GenAiTelemetry.GetActivitySource().StartActivity("document.chunking", ActivityKind.Internal))
            {
                var effectiveMode = ChunkingModeResolver.Resolve(options.ChunkingMode, doc);

                chunkActivity?.SetTag("chunking.requested_mode", options.ChunkingMode.ToString());
                chunkActivity?.SetTag("chunking.effective_mode", effectiveMode.ToString());
                chunkActivity?.SetTag("chunking.min_tokens", options.ChunkPolicy.MinTokens);
                chunkActivity?.SetTag("chunking.target_tokens", options.ChunkPolicy.TargetTokens);
                chunkActivity?.SetTag("chunking.max_tokens", options.ChunkPolicy.MaxTokens);

                IChunker chunker = effectiveMode == ChunkingMode.Semantic
                    ? new SemanticChunker(_tokenCounter, _embeddingRouter, options.EmbeddingModelName, options.SemanticSimilarityThreshold, _telemetry)
                    : new RecursiveChunker(_tokenCounter);

                chunkActivity?.SetTag("chunker.type", chunker.GetType().Name);

                chunks = await chunker.ChunkAsync(doc, options.ChunkPolicy, ct);

                chunkActivity?.SetTag("chunking.chunks.count", chunks.Count);
                chunkActivity?.SetTag("chunking.total_tokens", chunks.Sum(c => _tokenCounter.CountTokens(c.Text)));
                chunkActivity?.SetTag("chunking.avg_tokens", chunks.Count > 0 ? chunks.Average(c => _tokenCounter.CountTokens(c.Text)) : 0);
            }

            // ---- 5) Embeddings (optional)
            IReadOnlyList<EmbeddedChunk> embedded = Array.Empty<EmbeddedChunk>();
            if (options.EnableEmbedding && services.EmbeddingModel is not null)
            {
                using var embeddingActivity = GenAiTelemetry.GetActivitySource().StartActivity("document.embedding", ActivityKind.Internal);
                embeddingActivity?.SetTag("embedding.model_name", services.EmbeddingModel.Name);
                embeddingActivity?.SetTag("embedding.chunks.count", chunks.Count);
                embeddingActivity?.SetTag("embedding.dimensions", services.EmbeddingModel.Dimensions);

                var chunkTexts = chunks.Select(c => c.Text).ToList();
                var vectors = await services.EmbeddingModel.EmbedBatchAsync(chunkTexts, ct);

                var list = new List<EmbeddedChunk>(chunks.Count);
                int totalInputTokens = 0;

                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    totalInputTokens += chunks[ci].Text.Length / 4;
                    list.Add(new EmbeddedChunk(chunks[ci], services.EmbeddingModel.Name, vectors[ci]));
                }

                embedded = list;

                embeddingActivity?.SetTag("gen_ai.usage.input_tokens", totalInputTokens);
                embeddingActivity?.SetTag("embedding.vectors.count", embedded.Count);
                embeddingActivity?.SetTag("embedding.total_dimensions", embedded.Count * services.EmbeddingModel.Dimensions);
            }

            // ---- 6) Store (optional)
            var storeOptions = options.Store ?? new StoreOptions();
            if (storeOptions.EnableStore && embedded.Count > 0 && services.VectorStore is not null)
            {
                using var storeActivity = GenAiTelemetry.GetActivitySource().StartActivity("vector.store.upsert", ActivityKind.Client);
                storeActivity?.SetTag("vector_store.name", storeOptions.StoreName);
                storeActivity?.SetTag("vector_store.collection", storeOptions.Collection);
                storeActivity?.SetTag("vector_store.vectors.count", embedded.Count);
                storeActivity?.SetTag("vector_store.dimensions", embedded.FirstOrDefault()?.Vector.Length ?? 0);
                storeActivity?.SetTag("vector_store.type", services.VectorStore.GetType().Name);

                await services.VectorStore.UpsertAsync(storeOptions.Collection, embedded, ct);
            }

            // Final pipeline metrics
            var totalDuration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            pipelineActivity?.SetTag("pipeline.duration_seconds", totalDuration);
            pipelineActivity?.SetTag("pipeline.chunks.count", chunks.Count);
            pipelineActivity?.SetTag("pipeline.images.count", images.Count);
            pipelineActivity?.SetTag("pipeline.embedded.count", embedded.Count);
            pipelineActivity?.SetTag("pipeline.success", true);
            pipelineActivity?.SetStatus(ActivityStatusCode.Ok);

            return (doc, chunks, images, embedded);
        }
        catch (Exception ex)
        {
            pipelineActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            pipelineActivity?.SetTag("pipeline.success", false);
            pipelineActivity?.SetTag("error.type", ex.GetType().Name);
            pipelineActivity?.SetTag("error.message", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Applies OCR to images and replaces PDF image placeholders with recognized text.
    /// </summary>
    private async ValueTask<(ExtractedDocument Doc, IReadOnlyList<ImageArtifact> Images)>
        ApplyOcrAsync(ExtractedDocument doc, IReadOnlyList<ImageArtifact> images, IngestionOptions options, CancellationToken ct)
    {
        using var ocrActivity = GenAiTelemetry.GetActivitySource().StartActivity("ocr.processing", ActivityKind.Internal);
        ocrActivity?.SetTag("ocr.language", options.Images.OcrLanguage);
        ocrActivity?.SetTag("ocr.dpi", options.Images.OcrDpi);
        ocrActivity?.SetTag("ocr.images.count", images.Count);

        if (_ocrEngine is null)
            throw new InvalidOperationException("OCR is enabled but no IOcrEngine was provided to the pipeline.");

        int ocrSuccessCount = 0;
        int ocrTotalChars = 0;

        if (images.Count > 0)
        {
            var updatedImages = new List<ImageArtifact>(images.Count);
            var byPage = images.GroupBy(i => i.PageNumber).ToDictionary(g => g.Key, g => g.ToList());
            var newSections = new List<ExtractedSection>(doc.Sections.Count);

            foreach (var s in doc.Sections)
            {
                if (s.PageNumber is int page && byPage.TryGetValue(page, out var pageImages))
                {
                    var text = s.Text;

                    foreach (var img in pageImages)
                    {
                        if (img.Bytes is null || img.Bytes.Length == 0)
                        {
                            text = text.Replace($"[[PDF_IMAGE id={img.Id}]]", string.Empty, StringComparison.Ordinal);
                            updatedImages.Add(img);
                            continue;
                        }

                        using var ocrImageActivity = GenAiTelemetry.GetActivitySource().StartActivity("ocr.recognize_image", ActivityKind.Internal);
                        ocrImageActivity?.SetTag("ocr.image.id", img.Id);
                        ocrImageActivity?.SetTag("ocr.image.page", page);
                        ocrImageActivity?.SetTag("ocr.image.size_bytes", img.LengthBytes ?? 0);

                        var ocrText = await _ocrEngine.RecognizeAsync(img.Bytes, new OcrOptions(options.Images.OcrLanguage, options.Images.OcrDpi), ct);
                        var clean = (ocrText ?? string.Empty).Trim();

                        var md = img.Metadata is Dictionary<string, string> d
                            ? new Dictionary<string, string>(d)
                            : new Dictionary<string, string>(img.Metadata);

                        if (!string.IsNullOrWhiteSpace(clean))
                        {
                            ocrSuccessCount++;
                            ocrTotalChars += clean.Length;

                            ocrImageActivity?.SetTag("ocr.text.length", clean.Length);
                            ocrImageActivity?.SetTag("ocr.success", true);

                            md["ocrText"] = clean;
                            md["ocrLang"] = options.Images.OcrLanguage;

                            var replacement =
                                $"[IMAGE_OCR id={img.Id} page={page}]\n" +
                                clean + "\n" +
                                "[/IMAGE_OCR]";

                            text = text.Replace($"[[PDF_IMAGE id={img.Id}]]", replacement, StringComparison.Ordinal);
                        }
                        else
                        {
                            ocrImageActivity?.SetTag("ocr.success", false);
                            text = text.Replace($"[[PDF_IMAGE id={img.Id}]]", string.Empty, StringComparison.Ordinal);
                        }

                        updatedImages.Add(img with { Metadata = md });
                    }

                    newSections.Add(s with { Text = text });
                }
                else
                {
                    newSections.Add(s);
                }
            }

            doc = doc with { Sections = newSections };
            images = updatedImages;
        }

        ocrActivity?.SetTag("ocr.success.count", ocrSuccessCount);
        ocrActivity?.SetTag("ocr.total_chars", ocrTotalChars);

        return (doc, images);
    }
}
