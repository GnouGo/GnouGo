using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;
using DocIngestor.Core.Pipeline;
using GnOuGo.DocsIngestor.Mcp.Data;
using GnOuGo.DocsIngestor.Mcp.Models;

namespace GnOuGo.DocsIngestor.Mcp.Services;

public sealed class DocsIngestorMcpService
{
    private readonly UrlDownloadService _downloadService;
    private readonly DocumentIngestionPipeline _pipeline;
    private readonly KeyVaultEmbeddingConfigProvider _embeddingConfigProvider;
    private readonly IVectorSearchStore _searchStore;
    private readonly IVectorStoreAdmin _storeAdmin;
    private readonly StoredDocumentRepository _repository;
    private readonly OriginalDocumentStore _originalStore;

    public DocsIngestorMcpService(
        UrlDownloadService downloadService,
        DocumentIngestionPipeline pipeline,
        KeyVaultEmbeddingConfigProvider embeddingConfigProvider,
        IVectorSearchStore searchStore,
        IVectorStoreAdmin storeAdmin,
        StoredDocumentRepository repository,
        OriginalDocumentStore originalStore)
    {
        _downloadService = downloadService;
        _pipeline = pipeline;
        _embeddingConfigProvider = embeddingConfigProvider;
        _searchStore = searchStore;
        _storeAdmin = storeAdmin;
        _repository = repository;
        _originalStore = originalStore;
    }

    public async Task<IReadOnlyList<VectorizedFileResult>> VectorizeAsync(FileVectorizationRequest request, CancellationToken ct = default)
    {
        ValidateUrls(request.FileUrls);
        var results = new List<VectorizedFileResult>(request.FileUrls.Count);

        foreach (var url in request.FileUrls)
        {
            await using var downloaded = await _downloadService.DownloadAsync(url, ct);
            var (doc, chunks) = await ExtractChunksAsync(downloaded, ChunkingRequest.From(request), ct);
            results.Add(new VectorizedFileResult(
                downloaded.SourceUrl,
                downloaded.FileName,
                downloaded.ContentType,
                downloaded.SizeBytes,
                downloaded.Sha256,
                doc.DocumentId,
                doc.Metadata,
                chunks.OrderBy(c => c.Index).Select(ChunkDto.From).ToArray()));
        }

        return results;
    }

    public async Task<IReadOnlyList<IngestedFileResult>> IngestAsync(FileIngestionRequest request, Guid? keyVaultTenantId, CancellationToken ct = default)
    {
        ValidateUrls(request.FileUrls);
        var results = new List<IngestedFileResult>(request.FileUrls.Count);
        var embeddingModel = await _embeddingConfigProvider.ResolveAsync(request.EmbeddingConfigName, keyVaultTenantId, request.Author, ct);

        foreach (var url in request.FileUrls)
        {
            await using var downloaded = await _downloadService.DownloadAsync(url, ct);
            var existing = await _repository.GetBySourceAsync(request.TenantId, request.Collection, downloaded.SourceUrl, ct);
            if (existing is not null && string.Equals(existing.Sha256, downloaded.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new IngestedFileResult(
                    downloaded.SourceUrl,
                    existing.FileName,
                    existing.Id,
                    existing.Collection,
                    existing.EmbeddingConfigName,
                    existing.Sha256,
                    Skipped: true,
                    Action: "unchanged",
                    existing.ChunkCount,
                    "Document hash is unchanged; no ingestion was performed."));
                continue;
            }

            if (existing is not null)
                await DeleteStoredAsync(existing, ct);

            var (doc, chunks) = await ExtractChunksAsync(downloaded, ChunkingRequest.From(request), ct);
            var vectors = await embeddingModel.EmbedBatchAsync(chunks.Select(c => c.Text).ToArray(), ct);
            var embedded = chunks.Select((chunk, index) => new EmbeddedChunk(chunk, embeddingModel.Name, vectors[index])).ToArray();
            await _searchStore.UpsertAsync(request.Collection, embedded, ct);

            var originalPath = await _originalStore.SaveAsync(request.TenantId, request.Collection, doc.DocumentId, downloaded.FileName, downloaded.TempPath, ct);
            var now = DateTimeOffset.UtcNow;
            await _repository.UpsertAsync(new StoredDocumentRecord(
                doc.DocumentId,
                request.TenantId,
                downloaded.SourceUrl,
                downloaded.FileName,
                downloaded.ContentType,
                downloaded.SizeBytes,
                downloaded.Sha256,
                request.Collection,
                request.EmbeddingConfigName,
                originalPath,
                chunks.Count,
                existing?.CreatedUtc ?? now,
                now), ct);

            results.Add(new IngestedFileResult(
                downloaded.SourceUrl,
                downloaded.FileName,
                doc.DocumentId,
                request.Collection,
                request.EmbeddingConfigName,
                downloaded.Sha256,
                Skipped: false,
                Action: existing is null ? "created" : "replaced",
                chunks.Count,
                null));
        }

        return results;
    }

    public Task<IReadOnlyList<StoredDocumentRecord>> ListFilesAsync(string? tenantId, string? collection, CancellationToken ct = default)
        => _repository.ListAsync(tenantId, collection, ct);

    public async Task<IReadOnlyList<SearchHitDto>> SearchAsync(
        string query,
        string collection,
        string embeddingConfigName,
        Guid? keyVaultTenantId,
        string author,
        int topK,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query is required.", nameof(query));

        var model = await _embeddingConfigProvider.ResolveAsync(embeddingConfigName, keyVaultTenantId, author, ct);
        var queryVector = await model.EmbedAsync(query, ct);
        var results = await _searchStore.SearchAsync(collection, queryVector, Math.Max(1, topK), ct);
        return results.Select(r => new SearchHitDto(
            Math.Round(r.Score, 6),
            r.Chunk.Chunk.ChunkId,
            r.Chunk.Chunk.DocumentId,
            r.Chunk.Chunk.SectionId,
            r.Chunk.Chunk.Index,
            r.Chunk.Chunk.Text,
            r.Chunk.Chunk.Metadata,
            r.Chunk.EmbeddingModelName,
            r.Chunk.Vector.Length)).ToArray();
    }

    public async Task<OriginalDownloadDto?> DownloadOriginalAsync(string documentId, CancellationToken ct = default)
    {
        var record = await _repository.GetByIdAsync(documentId, ct);
        if (record is null || !File.Exists(record.OriginalPath))
            return null;

        var bytes = await File.ReadAllBytesAsync(record.OriginalPath, ct);
        return new OriginalDownloadDto(
            record.Id,
            record.TenantId,
            record.FileName,
            record.ContentType,
            record.SizeBytes,
            record.Sha256,
            Convert.ToBase64String(bytes));
    }

    public async Task<bool> DeleteFileAsync(string documentId, CancellationToken ct = default)
    {
        var record = await _repository.GetByIdAsync(documentId, ct);
        if (record is null)
            return false;

        await DeleteStoredAsync(record, ct);
        return true;
    }

    private async Task DeleteStoredAsync(StoredDocumentRecord record, CancellationToken ct)
    {
        await _storeAdmin.DeleteByDocumentAsync(record.Collection, record.Id, ct);
        await _originalStore.DeleteAsync(record.OriginalPath, ct);
        await _repository.DeleteAsync(record.Id, ct);
    }

    private async Task<(ExtractedDocument Doc, IReadOnlyList<TextChunk> Chunks)> ExtractChunksAsync(
        DownloadedDocument downloaded,
        ChunkingRequest request,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(downloaded.TempPath);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenantId"] = request.TenantId,
            ["sourceUrl"] = downloaded.SourceUrl,
            ["sha256"] = downloaded.Sha256,
        };

        await using var source = new DocumentSource(
            stream,
            downloaded.FileName,
            downloaded.ContentType,
            downloaded.SizeBytes,
            metadata,
            ownsStream: false);

        var options = CreateIngestionOptions(request);
        var (doc, chunks, _, _) = await _pipeline.RunAsync(source, options, ct);
        return (doc, chunks.OrderBy(c => c.Index).ToArray());
    }

    private static IngestionOptions CreateIngestionOptions(ChunkingRequest request)
    {
        var mode = request.ChunkingMode.Trim().ToLowerInvariant() switch
        {
            "semantic" => ChunkingMode.Semantic,
            "auto" => ChunkingMode.Auto,
            _ => ChunkingMode.Recursive,
        };

        return new IngestionOptions(
            ChunkingMode: mode,
            ChunkPolicy: new ChunkSizePolicy(request.MinTokens, request.TargetTokens, request.MaxTokens, request.OverlapTokens),
            EmbeddingModelName: "hash-384",
            EnableEmbedding: false,
            Images: new ImageExtractionOptions(
                EnableImageDiscovery: request.EnableImageDiscovery,
                LoadImageBytes: request.LoadImageBytes,
                EnableOcr: request.EnableOcr,
                OcrLanguage: request.OcrLanguage,
                OcrDpi: request.OcrDpi),
            Store: new StoreOptions(EnableStore: false));
    }

    private static void ValidateUrls(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0)
            throw new ArgumentException("At least one file URL is required.");
    }

    private sealed record ChunkingRequest(
        string TenantId,
        string ChunkingMode,
        int MinTokens,
        int TargetTokens,
        int MaxTokens,
        int OverlapTokens,
        bool EnableImageDiscovery,
        bool LoadImageBytes,
        bool EnableOcr,
        string OcrLanguage,
        int OcrDpi)
    {
        public static ChunkingRequest From(FileVectorizationRequest request) => new(
            request.TenantId,
            request.ChunkingMode,
            request.MinTokens,
            request.TargetTokens,
            request.MaxTokens,
            request.OverlapTokens,
            request.EnableImageDiscovery,
            request.LoadImageBytes,
            request.EnableOcr,
            request.OcrLanguage,
            request.OcrDpi);

        public static ChunkingRequest From(FileIngestionRequest request) => new(
            request.TenantId,
            request.ChunkingMode,
            request.MinTokens,
            request.TargetTokens,
            request.MaxTokens,
            request.OverlapTokens,
            request.EnableImageDiscovery,
            request.LoadImageBytes,
            request.EnableOcr,
            request.OcrLanguage,
            request.OcrDpi);
    }
}


