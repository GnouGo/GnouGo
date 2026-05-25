using System.ComponentModel;
using GnOuGo.DocIngestor.Mcp.Models;
using GnOuGo.DocIngestor.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace GnOuGo.DocIngestor.Mcp;

[McpServerToolType]
public sealed class DocsIngestorTools
{
    private readonly DocsIngestorMcpService _service;
    private readonly DocsIngestorMcpOptions _options;
    private readonly ILogger<DocsIngestorTools> _logger;

    public DocsIngestorTools(
        DocsIngestorMcpService service,
        IOptions<DocsIngestorMcpOptions> options,
        ILogger<DocsIngestorTools> logger)
    {
        _service = service;
        _options = options.Value;
        _logger = logger;
    }

    [McpServerTool(Name = "docs_ingestor_vectorize_files"), Description("Downloads one or more internal file URLs, extracts their binary content, vectorizes them into ordered text chunks, and returns chunks with metadata without storing them.")]
    public async Task<DocsIngestorResult> VectorizeFilesAsync(
        [Description("Absolute http/https URLs of files to download internally.")] string[] fileUrls,
        [Description("Tenant identifier propagated to document metadata. Omit for the configured default tenant.")] string? tenantId = null,
        [Description("Chunking mode: recursive, semantic, or auto.")] string? chunkingMode = null,
        [Description("Minimum chunk size in tokens.")] int? minTokens = null,
        [Description("Target chunk size in tokens.")] int? targetTokens = null,
        [Description("Maximum chunk size in tokens.")] int? maxTokens = null,
        [Description("Chunk overlap in tokens.")] int? overlapTokens = null,
        CancellationToken ct = default)
    {
        try
        {
            var request = CreateVectorizationRequest(fileUrls, tenantId, chunkingMode, minTokens, targetTokens, maxTokens, overlapTokens);
            return DocsIngestorResult.Ok(await _service.VectorizeAsync(request, ct), DocsIngestorJsonContext.Default.IReadOnlyListVectorizedFileResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "docs_ingestor_vectorize_files failed");
            return DocsIngestorResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "docs_ingestor_ingest_files"), Description("Downloads one or more internal file URLs, resolves the embedding configuration from KeyVault, stores originals, embeds chunks, and ingests them into the vector database. Unchanged documents are skipped by SHA-256 hash.")]
    public async Task<DocsIngestorResult> IngestFilesAsync(
        [Description("Absolute http/https URLs of files to download internally.")] string[] fileUrls,
        [Description("Vector collection name.")] string? collection = null,
        [Description("KeyVault secret key containing the selected embedding configuration. Built-in hash-384/hash-768 are accepted for local tests.")] string? embeddingConfigName = null,
        [Description("Tenant identifier for stored document metadata.")] string? tenantId = null,
        [Description("Optional KeyVault tenant id used to read embedding config secrets. Omit for default KeyVault tenant.")] Guid? keyVaultTenantId = null,
        [Description("Author/audit identity used when reading KeyVault secrets.")] string? author = null,
        [Description("Chunking mode: recursive, semantic, or auto.")] string? chunkingMode = null,
        [Description("Minimum chunk size in tokens.")] int? minTokens = null,
        [Description("Target chunk size in tokens.")] int? targetTokens = null,
        [Description("Maximum chunk size in tokens.")] int? maxTokens = null,
        [Description("Chunk overlap in tokens.")] int? overlapTokens = null,
        CancellationToken ct = default)
    {
        try
        {
            var vectorization = CreateVectorizationRequest(fileUrls, tenantId, chunkingMode, minTokens, targetTokens, maxTokens, overlapTokens);
            var request = new FileIngestionRequest(
                vectorization.FileUrls,
                vectorization.TenantId,
                string.IsNullOrWhiteSpace(collection) ? _options.DefaultCollection : collection!,
                string.IsNullOrWhiteSpace(embeddingConfigName) ? _options.DefaultEmbeddingConfigName : embeddingConfigName!.Trim(),
                vectorization.ChunkingMode,
                vectorization.MinTokens,
                vectorization.TargetTokens,
                vectorization.MaxTokens,
                vectorization.OverlapTokens,
                vectorization.EnableImageDiscovery,
                vectorization.LoadImageBytes,
                vectorization.EnableOcr,
                vectorization.OcrLanguage,
                vectorization.OcrDpi,
                string.IsNullOrWhiteSpace(author) ? _options.DefaultAuthor : author!);

            return DocsIngestorResult.Ok(await _service.IngestAsync(request, keyVaultTenantId, ct), DocsIngestorJsonContext.Default.IReadOnlyListIngestedFileResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "docs_ingestor_ingest_files failed");
            return DocsIngestorResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "docs_ingestor_list_files"), Description("Lists original files currently registered in the document ingestion vector database metadata store.")]
    public async Task<DocsIngestorResult> ListFilesAsync(
        [Description("Optional tenant id filter.")] string? tenantId = null,
        [Description("Optional collection filter.")] string? collection = null,
        CancellationToken ct = default)
    {
        try
        {
            return DocsIngestorResult.Ok(await _service.ListFilesAsync(tenantId, collection, ct), DocsIngestorJsonContext.Default.IReadOnlyListStoredDocumentRecord);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "docs_ingestor_list_files failed");
            return DocsIngestorResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "docs_ingestor_vector_search"), Description("Runs vector search over chunks. The query text is embedded using the embedding configuration read from KeyVault.")]
    public async Task<DocsIngestorResult> VectorSearchAsync(
        [Description("Text used to compute the query vector.")] string query,
        [Description("Vector collection name.")] string? collection = null,
        [Description("KeyVault secret key containing the embedding configuration. Built-in hash-384/hash-768 are accepted for local tests.")] string? embeddingConfigName = null,
        [Description("Optional KeyVault tenant id used to read embedding config secrets. Omit for default KeyVault tenant.")] Guid? keyVaultTenantId = null,
        [Description("Author/audit identity used when reading KeyVault secrets.")] string? author = null,
        [Description("Number of search hits to return.")] int topK = 10,
        CancellationToken ct = default)
    {
        try
        {
            var hits = await _service.SearchAsync(
                query,
                string.IsNullOrWhiteSpace(collection) ? _options.DefaultCollection : collection!,
                string.IsNullOrWhiteSpace(embeddingConfigName) ? _options.DefaultEmbeddingConfigName : embeddingConfigName!.Trim(),
                keyVaultTenantId,
                string.IsNullOrWhiteSpace(author) ? _options.DefaultAuthor : author!,
                topK,
                ct);
            return DocsIngestorResult.Ok(hits, DocsIngestorJsonContext.Default.IReadOnlyListSearchHitDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "docs_ingestor_vector_search failed");
            return DocsIngestorResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "docs_ingestor_download_original"), Description("Downloads the original file used for embedding. The content is returned as base64 for MCP transport compatibility.")]
    public async Task<DocsIngestorResult> DownloadOriginalAsync(
        [Description("Document id returned by ingest/list/search.")] string documentId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.DownloadOriginalAsync(documentId, ct);
            return result is null
                ? DocsIngestorResult.Fail("Original document was not found.")
                : DocsIngestorResult.Ok(result, DocsIngestorJsonContext.Default.OriginalDownloadDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "docs_ingestor_download_original failed for {DocumentId}", documentId);
            return DocsIngestorResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "docs_ingestor_delete_file"), Description("Deletes one stored original file and all associated chunks from its vector collection.")]
    public async Task<DocsIngestorResult> DeleteFileAsync(
        [Description("Document id returned by ingest/list/search.")] string documentId,
        CancellationToken ct = default)
    {
        try
        {
            var deleted = await _service.DeleteFileAsync(documentId, ct);
            return deleted
                ? DocsIngestorResult.Ok(new DeletedDocumentDto(true, documentId), DocsIngestorJsonContext.Default.DeletedDocumentDto)
                : DocsIngestorResult.Fail("Document was not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "docs_ingestor_delete_file failed for {DocumentId}", documentId);
            return DocsIngestorResult.Fail(ex.Message);
        }
    }

    private FileVectorizationRequest CreateVectorizationRequest(
        IReadOnlyList<string> fileUrls,
        string? tenantId,
        string? chunkingMode,
        int? minTokens,
        int? targetTokens,
        int? maxTokens,
        int? overlapTokens)
    {
        return new FileVectorizationRequest(
            fileUrls,
            string.IsNullOrWhiteSpace(tenantId) ? _options.DefaultTenantId : tenantId!,
            string.IsNullOrWhiteSpace(chunkingMode) ? _options.Chunking.Mode : chunkingMode!,
            minTokens ?? _options.Chunking.MinTokens,
            targetTokens ?? _options.Chunking.TargetTokens,
            maxTokens ?? _options.Chunking.MaxTokens,
            overlapTokens ?? _options.Chunking.OverlapTokens,
            _options.Images.EnableImageDiscovery,
            _options.Images.LoadImageBytes,
            _options.Images.EnableOcr,
            _options.Images.OcrLanguage,
            _options.Images.OcrDpi);
    }
}
