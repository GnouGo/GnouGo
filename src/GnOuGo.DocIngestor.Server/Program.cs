using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.Auth.Core;
using DocIngestor.Core.Embeddings;
using DocIngestor.Core.Extractors;
using DocIngestor.Core.Images;
using DocIngestor.Core.Ocr;
using DocIngestor.Core.Pipeline;
using DocIngestor.Core.Stores;
using DocIngestor.Core.Telemetry;
using DocIngestor.Core.Tokenization;
using DocIngestor.Core.Reranking;
using DocIngestor.Server.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateSlimBuilder(args);

// ── Typed configuration ─────────────────────────────────────────────
var settings = builder.Configuration.GetSection("DocIngestor").Get<DocIngestorServerSettings>() ?? new();
builder.Services.AddSingleton(settings);

var openAiSettings = builder.Configuration.GetSection("OpenAi").Get<OpenAiSettings>() ?? new();
builder.Services.AddSingleton(openAiSettings);

var ollamaSettings = builder.Configuration.GetSection("Ollama").Get<OllamaSettings>() ?? new();
builder.Services.AddSingleton(ollamaSettings);

var otelSettings = builder.Configuration.GetSection("OpenTelemetry").Get<OpenTelemetrySettings>() ?? new();
builder.Services.AddSingleton(otelSettings);

// ── OpenTelemetry ───────────────────────────────────────────────────
if (otelSettings.Enabled)
{
    var protocol = otelSettings.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase)
        ? OtlpExportProtocol.HttpProtobuf
        : OtlpExportProtocol.Grpc;

    var resourceBuilder = ResourceBuilder
        .CreateDefault()
        .AddService(otelSettings.ServiceName, serviceVersion: "1.0.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["service.namespace"] = "GnOuGo-agent",
            ["host.name"] = Environment.MachineName,
        });

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(resourceBuilder)
                .AddSource(GenAiTelemetry.GetActivitySource().Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                    o.Protocol = protocol;
                    o.ExportProcessorType = OpenTelemetry.ExportProcessorType.Simple;
                    if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                        o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                });
        })
        .WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(GenAiTelemetry.GetMeter().Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                    o.Protocol = protocol;
                    if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                        o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                });
        });
}

// GenAiTelemetry (used by pipeline regardless of OTLP export)
builder.Services.AddSingleton<GenAiTelemetry>();


// ── Core services ───────────────────────────────────────────────────
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ITokenCounter, DefaultTokenCounter>();

// Extractors (binary-format first, plain-text catch-all last)
builder.Services.AddSingleton<IDocumentTextExtractor, PdfPigExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocxOpenXmlExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, PptxOpenXmlExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, XlsxOpenXmlExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();

// Image extractors
builder.Services.AddSingleton<IImageExtractor, DocxImageExtractor>();
builder.Services.AddSingleton<IImageExtractor, PptxImageExtractor>();
builder.Services.AddSingleton<IImageExtractor, PdfPigImageExtractor>();
builder.Services.AddSingleton<IImageExtractor, XlsxImageExtractor>();

// Router & pipeline
builder.Services.AddSingleton<DocumentRouter>();
builder.Services.AddSingleton<DocumentIngestionPipeline>();

// API Key provider (if configured)
if (!string.IsNullOrWhiteSpace(openAiSettings.ApiKey))
{
    builder.Services.AddSingleton<IApiKeyProvider>(new StaticApiKeyProvider(openAiSettings.ApiKey));
}
else if (!string.IsNullOrWhiteSpace(openAiSettings.Issuer) &&
         !string.IsNullOrWhiteSpace(openAiSettings.ClientId) &&
         !string.IsNullOrWhiteSpace(openAiSettings.Scopes) &&
         !string.IsNullOrWhiteSpace(openAiSettings.ClientSecret))
{
    builder.Services.AddSingleton<IApiKeyProvider>(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        return new OidcJwtApiKeyProvider(httpClient,
            new OidcClientCredentialsConfig(
                Issuer: openAiSettings.Issuer,
                ClientId: openAiSettings.ClientId,
                Scopes: openAiSettings.Scopes,
                ClientSecret: openAiSettings.ClientSecret,
                PrivateKeyPem: null));
    });
}

// Embedding models
builder.Services.AddSingleton<IEmbeddingRouter>(sp =>
{
    var models = new List<IEmbeddingModel>
    {
        new HashEmbeddingModel("hash-384", 384),
        new HashEmbeddingModel("hash-768", 768),
    };

    var apiKeyProvider = sp.GetService<IApiKeyProvider>();
    if (apiKeyProvider is not null)
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var telemetry = sp.GetService<GenAiTelemetry>();
        models.Add(new OpenAiCompatibleEmbeddingModel(
            name: "ada3-large",
            endpointUrl: openAiSettings.EndpointUrl,
            model: "text-embedding-3-large",
            apiKeyProvider: apiKeyProvider,
            http: httpClient,
            defaultDims: 3072,
            telemetry: telemetry));
    }

    // Ollama local embedding model
    var ollama = sp.GetRequiredService<OllamaSettings>();
    if (ollama.Enabled)
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        models.Add(new OllamaEmbeddingModel(
            name: $"ollama-{ollama.EmbeddingModel}",
            baseUrl: ollama.BaseUrl,
            model: ollama.EmbeddingModel,
            http: httpClient,
            defaultDims: ollama.DefaultDimensions));
    }

    return new EmbeddingRegistry(models);
});

// Vector store (SQLite) — single instance shared across the app
builder.Services.AddSingleton<SqliteCosineVectorStore>(sp =>
{
    var cfg = sp.GetRequiredService<DocIngestorServerSettings>();
    var sqlitePath = Path.Combine(cfg.Store.StoreDirectory, "vectors.sqlite");
    return new SqliteCosineVectorStore(sqlitePath);
});
builder.Services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<SqliteCosineVectorStore>());
builder.Services.AddSingleton<IVectorSearchStore>(sp => sp.GetRequiredService<SqliteCosineVectorStore>());
builder.Services.AddSingleton<IVectorStoreAdmin>(sp => sp.GetRequiredService<SqliteCosineVectorStore>());
builder.Services.AddSingleton<IVectorStoreRouter>(sp =>
    new VectorStoreRegistry(new IVectorStore[] { sp.GetRequiredService<SqliteCosineVectorStore>() }));

// Rerankers
builder.Services.AddSingleton<IRerankerRouter>(sp =>
{
    var rerankers = new List<IReranker> { new Bm25Reranker() };

    var telemetryForRerankers = sp.GetService<GenAiTelemetry>();

    // OpenAI cross-encoder (if API key is available)
    var apiKeyProvider = sp.GetService<IApiKeyProvider>();
    if (apiKeyProvider is not null)
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var scorer = new OpenAiChatScorer(
            endpointUrl: openAiSettings.EndpointUrl,
            model: "gpt-4o-mini",
            apiKeyProvider: apiKeyProvider,
            http: httpClient,
            telemetry: telemetryForRerankers);
        rerankers.Add(new CrossEncoderReranker(scorer, maxConcurrency: 5));
    }

    // Ollama cross-encoder (if Ollama is enabled)
    var ollama = sp.GetRequiredService<OllamaSettings>();
    if (ollama.Enabled)
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var scorer = new OllamaChatScorer(
            baseUrl: ollama.BaseUrl,
            model: ollama.ChatModel,
            http: httpClient,
            telemetry: telemetryForRerankers);
        rerankers.Add(new CrossEncoderReranker(scorer, maxConcurrency: 3));
    }

    return new RerankerRegistry(rerankers);
});

// OCR Engine — resolved based on settings (default: OpenAI Vision)
builder.Services.AddSingleton<IOcrEngine>(sp =>
{
    var cfg = sp.GetRequiredService<DocIngestorServerSettings>();
    var engine = cfg.Images.Ocr.Engine?.ToLowerInvariant() ?? "openai";

    if (engine == "ollama")
    {
        var ollama = sp.GetRequiredService<OllamaSettings>();
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var telemetryForOcr = sp.GetService<GenAiTelemetry>();
        return new OllamaVisionOcrEngine(
            baseUrl: ollama.BaseUrl,
            model: ollama.VisionModel,
            http: httpClient,
            telemetry: telemetryForOcr);
    }
    else
    {
        // Default: OpenAI Vision
        var apiKeyProvider = sp.GetService<IApiKeyProvider>();
        if (apiKeyProvider is null)
            return new FakeOcrEngine(); // Fallback when no API key is configured

        var openAi = sp.GetRequiredService<OpenAiSettings>();
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var telemetryForOcr = sp.GetService<GenAiTelemetry>();
        return new OpenAiVisionOcrEngine(
            endpointUrl: openAi.EndpointUrl,
            model: "gpt-4o-mini",
            apiKeyProvider: apiKeyProvider,
            http: httpClient,
            telemetry: telemetryForOcr);
    }
});

// CORS
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();

// ── JSON options for responses ──────────────────────────────────────
var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

// ── API Endpoints ───────────────────────────────────────────────────

// GET /api/settings — return current default settings (so the UI can pre-fill)
app.MapGet("/api/settings", (DocIngestorServerSettings cfg, IRerankerRouter rerankerRouter) => Results.Json(new
{
    chunking = new { cfg.Chunking.Mode, cfg.Chunking.MinTokens, cfg.Chunking.TargetTokens, cfg.Chunking.MaxTokens, cfg.Chunking.OverlapTokens },
    embedding = new { cfg.Embedding.Enabled, cfg.Embedding.DefaultModel },
    store = new { cfg.Store.Enabled, cfg.Store.DefaultStore, cfg.Store.DefaultCollection },
    images = new { cfg.Images.Enabled, cfg.Images.LoadBytes, ocr = new { cfg.Images.Ocr.Enabled, cfg.Images.Ocr.Language, cfg.Images.Ocr.Dpi } },
    search = new {
        cfg.Search.DefaultTopK,
        reranker = new {
            cfg.Search.Reranker.Enabled,
            cfg.Search.Reranker.DefaultType,
            cfg.Search.Reranker.VectorWeight,
            cfg.Search.Reranker.RerankWeight,
            availableTypes = rerankerRouter.Available,
        },
    },
}, jsonOpts));

// POST /api/ingest — upload file + optional config overrides
app.MapPost("/api/ingest", async (HttpRequest req,
    DocIngestorServerSettings cfg,
    DocumentIngestionPipeline pipeline,
    SqliteCosineVectorStore store,
    ILogger<Program> logger) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data" });

    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Missing 'file' in form data" });

    // Parse optional JSON config from "options" form field
    var optionsJson = form["options"].FirstOrDefault();
    var overrides = optionsJson is not null
        ? JsonSerializer.Deserialize<IngestOverrides>(optionsJson, jsonOpts) ?? new()
        : new IngestOverrides();

    // Resolve values: override ?? appsettings default
    var collection = !string.IsNullOrWhiteSpace(overrides.Collection) ? overrides.Collection : cfg.Store.DefaultCollection;
    var chunkMode = !string.IsNullOrWhiteSpace(overrides.ChunkingMode) ? overrides.ChunkingMode : cfg.Chunking.Mode;
    var minTokens = overrides.MinTokens ?? cfg.Chunking.MinTokens;
    var targetTokens = overrides.TargetTokens ?? cfg.Chunking.TargetTokens;
    var maxTokens = overrides.MaxTokens ?? cfg.Chunking.MaxTokens;
    var overlapTokens = overrides.OverlapTokens ?? cfg.Chunking.OverlapTokens;
    var embedModel = !string.IsNullOrWhiteSpace(overrides.EmbeddingModel) ? overrides.EmbeddingModel : cfg.Embedding.DefaultModel;
    var embedEnabled = overrides.EmbeddingEnabled ?? cfg.Embedding.Enabled;
    var storeEnabled = overrides.StoreEnabled ?? cfg.Store.Enabled;
    var imagesEnabled = overrides.ImagesEnabled ?? cfg.Images.Enabled;

    // Build DocumentSource from upload stream (buffer into MemoryStream)
    var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    ms.Position = 0;
    var source = new DocumentSource(ms, file.FileName, file.ContentType, ms.Length, ownsStream: true);

    // Build ingestion options
    var mode = chunkMode.ToLowerInvariant() switch
    {
        "semantic" => ChunkingMode.Semantic,
        "recursive" => ChunkingMode.Recursive,
        "auto" => ChunkingMode.Auto,
        _ => ChunkingMode.Auto,
    };

    var ingestionOptions = new IngestionOptions(
        ChunkingMode: mode,
        ChunkPolicy: new ChunkSizePolicy(minTokens, targetTokens, maxTokens, overlapTokens),
        EmbeddingModelName: embedModel,
        SemanticSimilarityThreshold: 0.80,
        EnableEmbedding: embedEnabled,
        Images: new ImageExtractionOptions(
            EnableImageDiscovery: imagesEnabled,
            LoadImageBytes: imagesEnabled,
            EnableOcr: false
        ),
        Store: new StoreOptions(
            EnableStore: storeEnabled,
            StoreName: cfg.Store.DefaultStore,
            Collection: collection
        )
    );

    await using (source)
    {
        // 1) Delete previous data for this file name BEFORE ingestion
        //    DocumentId format is "{fileName}:{sha256-12chars}" so we match by prefix
        if (storeEnabled)
        {
            var deleted = await store.DeleteByDocumentPrefixAsync(collection, file.FileName + ":");
            if (deleted > 0)
                logger.LogInformation("Deleted {Count} old vectors for file '{FileName}' in collection '{Collection}'", deleted, file.FileName, collection);
        }

        // 2) Run extraction + embedding + store
        var (doc, chunks, images, embedded) = await pipeline.RunAsync(source, ingestionOptions);

        logger.LogInformation("[Ingested] {FileName} → doc={DocId} chunks={Chunks} images={Images} embedded={Embedded}",
            file.FileName, doc.DocumentId, chunks.Count, images.Count, embedded.Count);

        return Results.Ok(new
        {
            fileName = file.FileName,
            documentId = doc.DocumentId,
            collection,
            chunksCount = chunks.Count,
            imagesCount = images.Count,
            embeddedCount = embedded.Count,
            metadata = doc.Metadata,
        });
    }
});

// GET /api/search?collection=&query=&topK=&reranker=&rerankEnabled=&vectorWeight=&rerankWeight=
app.MapGet("/api/search", async (
    string? collection,
    string? query,
    int? topK,
    bool? rerankEnabled,
    string? reranker,
    double? vectorWeight,
    double? rerankWeight,
    DocIngestorServerSettings cfg,
    IVectorSearchStore searchStore,
    IEmbeddingRouter embeddingRouter,
    IRerankerRouter rerankerRouter) =>
{
    var col = !string.IsNullOrWhiteSpace(collection) ? collection : cfg.Store.DefaultCollection;
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "Missing 'query' parameter" });

    var k = topK ?? cfg.Search.DefaultTopK;
    var model = embeddingRouter.Get(cfg.Embedding.DefaultModel);
    var queryVec = await model.EmbedAsync(query);

    // Fetch more candidates when reranking so the reranker has a broader pool
    var useRerank = rerankEnabled ?? cfg.Search.Reranker.Enabled;
    var fetchK = useRerank ? Math.Max(k * 3, 30) : k;

    var results = await searchStore.SearchAsync(col, queryVec, fetchK);

    // Apply reranker if enabled
    if (useRerank && results.Count > 0)
    {
        var rerankerName = !string.IsNullOrWhiteSpace(reranker) ? reranker : cfg.Search.Reranker.DefaultType;
        var rerankImpl = rerankerRouter.Get(rerankerName);
        var opts = new RerankerOptions(
            TopK: k,
            VectorWeight: vectorWeight ?? cfg.Search.Reranker.VectorWeight,
            RerankWeight: rerankWeight ?? cfg.Search.Reranker.RerankWeight);
        results = await rerankImpl.RerankAsync(query, results, opts);
    }
    else if (results.Count > k)
    {
        results = results.Take(k).ToList();
    }

    return Results.Json(results.Select(r => new
    {
        score = Math.Round(r.Score, 6),
        chunkId = r.Chunk.Chunk.ChunkId,
        documentId = r.Chunk.Chunk.DocumentId,
        sectionId = r.Chunk.Chunk.SectionId,
        index = r.Chunk.Chunk.Index,
        text = r.Chunk.Chunk.Text,
        metadata = r.Chunk.Chunk.Metadata,
        embeddingModel = r.Chunk.EmbeddingModelName,
        dimensions = r.Chunk.Vector.Length,
    }), jsonOpts);
});

// GET /api/search/tsne — same as /api/search but returns 2D t-SNE coordinates for embedding visualization
app.MapGet("/api/search/tsne", async (
    string? collection,
    string? query,
    int? topK,
    bool? rerankEnabled,
    string? reranker,
    double? vectorWeight,
    double? rerankWeight,
    int? perplexity,
    DocIngestorServerSettings cfg,
    IVectorSearchStore searchStore,
    IEmbeddingRouter embeddingRouter,
    IRerankerRouter rerankerRouter) =>
{
    var col = !string.IsNullOrWhiteSpace(collection) ? collection : cfg.Store.DefaultCollection;
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "Missing 'query' parameter" });

    var k = topK ?? cfg.Search.DefaultTopK;
    var model = embeddingRouter.Get(cfg.Embedding.DefaultModel);
    var queryVec = await model.EmbedAsync(query);

    var useRerank = rerankEnabled ?? cfg.Search.Reranker.Enabled;
    var fetchK = useRerank ? Math.Max(k * 3, 30) : k;

    var results = await searchStore.SearchAsync(col, queryVec, fetchK);

    if (useRerank && results.Count > 0)
    {
        var rerankerName = !string.IsNullOrWhiteSpace(reranker) ? reranker : cfg.Search.Reranker.DefaultType;
        var rerankImpl = rerankerRouter.Get(rerankerName);
        var opts = new RerankerOptions(
            TopK: k,
            VectorWeight: vectorWeight ?? cfg.Search.Reranker.VectorWeight,
            RerankWeight: rerankWeight ?? cfg.Search.Reranker.RerankWeight);
        results = await rerankImpl.RerankAsync(query, results, opts);
    }
    else if (results.Count > k)
    {
        results = results.Take(k).ToList();
    }

    if (results.Count == 0)
        return Results.Json(new { hits = Array.Empty<object>(), queryPoint = new { x = 0.0, y = 0.0 } }, jsonOpts);

    // Build vectors array: query vector + result vectors
    var n = results.Count + 1; // +1 for the query
    var vectors = new float[n][];
    vectors[0] = queryVec;
    for (int i = 0; i < results.Count; i++)
        vectors[i + 1] = results[i].Chunk.Vector;

    // Run t-SNE to get 2D coordinates
    var coords = TsneHelper.Compute(vectors, perplexity: perplexity ?? Math.Min(Math.Max(5, n / 3), 50));

    return Results.Json(new
    {
        queryPoint = new { x = Math.Round(coords[0, 0], 4), y = Math.Round(coords[0, 1], 4) },
        hits = results.Select((r, i) => new
        {
            score = Math.Round(r.Score, 6),
            chunkId = r.Chunk.Chunk.ChunkId,
            documentId = r.Chunk.Chunk.DocumentId,
            sectionId = r.Chunk.Chunk.SectionId,
            index = r.Chunk.Chunk.Index,
            text = r.Chunk.Chunk.Text,
            metadata = r.Chunk.Chunk.Metadata,
            embeddingModel = r.Chunk.EmbeddingModelName,
            dimensions = r.Chunk.Vector.Length,
            x = Math.Round(coords[i + 1, 0], 4),
            y = Math.Round(coords[i + 1, 1], 4),
        }),
    }, jsonOpts);
});

// GET /api/collections
app.MapGet("/api/collections", async (IVectorStoreAdmin admin) =>
{
    var collections = await admin.ListCollectionsAsync();
    return Results.Ok(collections);
});

// GET /api/documents?collection=
app.MapGet("/api/documents", async (string? collection, DocIngestorServerSettings cfg, IVectorStoreAdmin admin) =>
{
    var col = !string.IsNullOrWhiteSpace(collection) ? collection : cfg.Store.DefaultCollection;
    var docs = await admin.ListDocumentsAsync(col);
    return Results.Ok(docs);
});

// DELETE /api/documents/{collection}/{documentId}
app.MapDelete("/api/documents/{collection}/{documentId}", async (string collection, string documentId, IVectorStoreAdmin admin) =>
{
    var deleted = await admin.DeleteByDocumentAsync(collection, documentId);
    return Results.Ok(new { deleted, collection, documentId });
});

// ── Static files ────────────────────────────────────────────────────
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

// ── DTO for ingest overrides ────────────────────────────────────────
internal sealed class IngestOverrides
{
    public string? Collection { get; set; }
    public string? ChunkingMode { get; set; }
    public int? MinTokens { get; set; }
    public int? TargetTokens { get; set; }
    public int? MaxTokens { get; set; }
    public int? OverlapTokens { get; set; }
    public string? EmbeddingModel { get; set; }
    public bool? EmbeddingEnabled { get; set; }
    public bool? StoreEnabled { get; set; }
    public bool? ImagesEnabled { get; set; }
}

/// <summary>
/// Minimal t-SNE implementation (exact, O(n²)) suitable for small result sets (≤ 200 points).
/// Based on van der Maaten & Hinton (2008).
/// </summary>
internal static class TsneHelper
{
    /// <summary>
    /// Compute 2D t-SNE embedding from high-dimensional vectors.
    /// Returns an n×2 array of coordinates.
    /// </summary>
    public static double[,] Compute(float[][] vectors, int perplexity = 15, int maxIter = 500, double learningRate = 200.0)
    {
        var n = vectors.Length;
        if (n <= 1)
            return new double[,] { { 0.0, 0.0 } };

        if (n == 2)
            return new double[,] { { -1.0, 0.0 }, { 1.0, 0.0 } };

        // 1) Compute pairwise squared Euclidean distances
        var dist2 = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double d = 0;
                var vi = vectors[i];
                var vj = vectors[j];
                var dims = Math.Min(vi.Length, vj.Length);
                for (int k = 0; k < dims; k++)
                {
                    double diff = vi[k] - vj[k];
                    d += diff * diff;
                }
                dist2[i, j] = d;
                dist2[j, i] = d;
            }
        }

        // 2) Compute symmetric P matrix using binary search for sigma
        var P = ComputeJointProbabilities(dist2, n, perplexity);

        // Early exaggeration
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                P[i, j] *= 4.0;

        // 3) Initialize Y randomly (small values)
        var rng = new Random(42);
        var Y = new double[n, 2];
        for (int i = 0; i < n; i++)
        {
            Y[i, 0] = rng.NextDouble() * 0.0001 - 0.00005;
            Y[i, 1] = rng.NextDouble() * 0.0001 - 0.00005;
        }

        // 4) Gradient descent with momentum
        var gains = new double[n, 2];
        var yInc = new double[n, 2];
        for (int i = 0; i < n; i++)
        {
            gains[i, 0] = 1.0;
            gains[i, 1] = 1.0;
        }

        for (int iter = 0; iter < maxIter; iter++)
        {
            // Remove early exaggeration after 100 iterations
            if (iter == 100)
            {
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        P[i, j] /= 4.0;
            }

            var momentum = iter < 250 ? 0.5 : 0.8;

            // Compute Q distribution (Student t with 1 DOF)
            var qNum = new double[n, n];
            double qSum = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double dy0 = Y[i, 0] - Y[j, 0];
                    double dy1 = Y[i, 1] - Y[j, 1];
                    double val = 1.0 / (1.0 + dy0 * dy0 + dy1 * dy1);
                    qNum[i, j] = val;
                    qNum[j, i] = val;
                    qSum += 2 * val;
                }
            }
            if (qSum < 1e-12) qSum = 1e-12;

            // Compute gradients
            var gradY = new double[n, 2];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double q = qNum[i, j] / qSum;
                    double mult = (P[i, j] - q) * qNum[i, j];
                    gradY[i, 0] += 4.0 * mult * (Y[i, 0] - Y[j, 0]);
                    gradY[i, 1] += 4.0 * mult * (Y[i, 1] - Y[j, 1]);
                }
            }

            // Update with adaptive gains and momentum
            for (int i = 0; i < n; i++)
            {
                for (int d = 0; d < 2; d++)
                {
                    bool sameSign = (gradY[i, d] > 0) == (yInc[i, d] > 0);
                    gains[i, d] = sameSign ? gains[i, d] * 0.8 : gains[i, d] + 0.2;
                    if (gains[i, d] < 0.01) gains[i, d] = 0.01;

                    yInc[i, d] = momentum * yInc[i, d] - learningRate * gains[i, d] * gradY[i, d];
                    Y[i, d] += yInc[i, d];
                }
            }

            // Center Y
            double meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++)
            {
                meanX += Y[i, 0];
                meanY += Y[i, 1];
            }
            meanX /= n;
            meanY /= n;
            for (int i = 0; i < n; i++)
            {
                Y[i, 0] -= meanX;
                Y[i, 1] -= meanY;
            }
        }

        return Y;
    }

    private static double[,] ComputeJointProbabilities(double[,] dist2, int n, int perplexity)
    {
        var P = new double[n, n];
        var targetEntropy = Math.Log(perplexity);

        for (int i = 0; i < n; i++)
        {
            double betaMin = double.NegativeInfinity;
            double betaMax = double.PositiveInfinity;
            double beta = 1.0; // 1 / (2 * sigma^2)

            for (int attempt = 0; attempt < 50; attempt++)
            {
                // Compute conditional probabilities p(j|i)
                double sumExp = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    sumExp += Math.Exp(-dist2[i, j] * beta);
                }
                if (sumExp < 1e-30) sumExp = 1e-30;

                double entropy = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i)
                    {
                        P[i, j] = 0;
                        continue;
                    }
                    double pji = Math.Exp(-dist2[i, j] * beta) / sumExp;
                    P[i, j] = pji;
                    if (pji > 1e-12)
                        entropy -= pji * Math.Log(pji);
                }

                double entropyDiff = entropy - targetEntropy;
                if (Math.Abs(entropyDiff) < 1e-5) break;

                if (entropyDiff > 0)
                {
                    betaMin = beta;
                    beta = double.IsPositiveInfinity(betaMax) ? beta * 2 : (beta + betaMax) / 2;
                }
                else
                {
                    betaMax = beta;
                    beta = double.IsNegativeInfinity(betaMin) ? beta / 2 : (beta + betaMin) / 2;
                }
            }
        }

        // Symmetrize: P_ij = (p(j|i) + p(i|j)) / (2n)
        var Psym = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double val = (P[i, j] + P[j, i]) / (2.0 * n);
                // Floor to avoid numerical issues
                if (val < 1e-12) val = 1e-12;
                Psym[i, j] = val;
                Psym[j, i] = val;
            }
        }
        return Psym;
    }
}

