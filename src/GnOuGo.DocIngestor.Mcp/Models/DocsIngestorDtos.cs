using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DocIngestor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.DocIngestor.Mcp.Models;

public sealed record DocsIngestorResult(bool Success, JsonElement? Data = null, string? Error = null)
{
    public static DocsIngestorResult Ok<T>(T data, JsonTypeInfo<T> typeInfo) =>
        new(true, JsonSerializer.SerializeToElement(data, typeInfo));

    public static DocsIngestorResult Fail(string error) => new(false, null, error);
}

public sealed record DeletedDocumentDto(bool Deleted, string DocumentId);

public sealed record HealthCheckResponse(string Status);

public sealed record DownloadedDocument(
    string SourceUrl,
    string TempPath,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256) : IAsyncDisposable
{
    public ILogger Logger { get; init; } = NullLogger.Instance;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(TempPath))
                File.Delete(TempPath);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to delete temporary downloaded document '{TempPath}'.", TempPath);
            // Best-effort temporary file cleanup.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed record StoredDocumentRecord(
    string Id,
    string TenantId,
    string SourceUrl,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256,
    string Collection,
    string EmbeddingConfigName,
    string OriginalPath,
    int ChunkCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? OriginalPathRelative = null);

public sealed record FileVectorizationRequest(
    IReadOnlyList<string> FileUrls,
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
    int OcrDpi);

public sealed record FileIngestionRequest(
    IReadOnlyList<string> FileUrls,
    string TenantId,
    string Collection,
    string EmbeddingConfigName,
    string ChunkingMode,
    int MinTokens,
    int TargetTokens,
    int MaxTokens,
    int OverlapTokens,
    bool EnableImageDiscovery,
    bool LoadImageBytes,
    bool EnableOcr,
    string OcrLanguage,
    int OcrDpi,
    string Author);

public sealed record VectorizedFileResult(
    string SourceUrl,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256,
    string DocumentId,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<ChunkDto> Chunks);

public sealed record IngestedFileResult(
    string SourceUrl,
    string FileName,
    string DocumentId,
    string Collection,
    string EmbeddingConfigName,
    string Sha256,
    bool Skipped,
    string Action,
    int ChunkCount,
    string? Message);

public sealed record ChunkDto(
    string ChunkId,
    string DocumentId,
    string SectionId,
    int Index,
    string Text,
    IReadOnlyDictionary<string, string> Metadata,
    string? Markdown,
    string? CsvLike)
{
    public static ChunkDto From(TextChunk chunk) => new(
        chunk.ChunkId,
        chunk.DocumentId,
        chunk.SectionId,
        chunk.Index,
        chunk.Text,
        chunk.Metadata,
        chunk.Markdown,
        chunk.CsvLike);
}

public sealed record SearchHitDto(
    double Score,
    string ChunkId,
    string DocumentId,
    string SectionId,
    int Index,
    string Text,
    IReadOnlyDictionary<string, string> Metadata,
    string EmbeddingModelName,
    int Dimensions);

public sealed record OriginalDownloadDto(
    string DocumentId,
    string TenantId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256,
    string Base64Content);

public sealed record EmbeddingConfig(
    string Provider,
    string? Name,
    string? Model,
    string? EndpointUrl,
    string? BaseUrl,
    string? ApiKey,
    string? ApiKeySecretKey,
    int? Dimensions);

internal static class DocsIngestorMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, DocsIngestorJsonContext.Default);
        return options;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(EmbeddingConfig))]
[JsonSerializable(typeof(DocsIngestorResult))]
[JsonSerializable(typeof(HealthCheckResponse))]
[JsonSerializable(typeof(DocsIngestorMcpOptions))]
[JsonSerializable(typeof(ChunkingOptions))]
[JsonSerializable(typeof(ImagesOptions))]
[JsonSerializable(typeof(DeletedDocumentDto))]
[JsonSerializable(typeof(FileVectorizationRequest))]
[JsonSerializable(typeof(FileIngestionRequest))]
[JsonSerializable(typeof(VectorizedFileResult))]
[JsonSerializable(typeof(IngestedFileResult))]
[JsonSerializable(typeof(StoredDocumentRecord))]
[JsonSerializable(typeof(SearchHitDto))]
[JsonSerializable(typeof(OriginalDownloadDto))]
[JsonSerializable(typeof(ChunkDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<VectorizedFileResult>))]
[JsonSerializable(typeof(IReadOnlyList<IngestedFileResult>))]
[JsonSerializable(typeof(IReadOnlyList<StoredDocumentRecord>))]
[JsonSerializable(typeof(IReadOnlyList<SearchHitDto>))]
[JsonSerializable(typeof(IReadOnlyList<ChunkDto>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal partial class DocsIngestorJsonContext : JsonSerializerContext;
