﻿﻿namespace DocIngestor.Server.Configuration;

/// <summary>Typed settings for the DocIngestor Server, bound from appsettings.json "DocIngestor" section.</summary>
public sealed class DocIngestorServerSettings
{
    public ChunkingSettings Chunking { get; set; } = new();
    public EmbeddingSettings Embedding { get; set; } = new();
    public StoreSettings Store { get; set; } = new();
    public ImagesSettings Images { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
}

public sealed class ChunkingSettings
{
    public string Mode { get; set; } = "auto";
    public int MinTokens { get; set; } = 400;
    public int TargetTokens { get; set; } = 1200;
    public int MaxTokens { get; set; } = 2000;
    public int OverlapTokens { get; set; } = 400;
}

public sealed class EmbeddingSettings
{
    public bool Enabled { get; set; } = true;
    public string DefaultModel { get; set; } = "hash-384";
}

public sealed class StoreSettings
{
    public bool Enabled { get; set; } = true;
    public string DefaultStore { get; set; } = "sqlite";
    public string StoreDirectory { get; set; } = "data/store";
    public string DefaultCollection { get; set; } = "default";
}

public sealed class ImagesSettings
{
    public bool Enabled { get; set; } = false;
    public bool LoadBytes { get; set; } = false;
    public OcrSettings Ocr { get; set; } = new();
}

public sealed class OcrSettings
{
    public bool Enabled { get; set; } = false;
    public string Engine { get; set; } = "openai";
    public string Language { get; set; } = "eng";
    public int Dpi { get; set; } = 300;
}

public sealed class SearchSettings
{
    public int DefaultTopK { get; set; } = 10;
    public RerankerSettings Reranker { get; set; } = new();
}

public sealed class RerankerSettings
{
    public bool Enabled { get; set; } = false;
    public string DefaultType { get; set; } = "bm25";
    public double VectorWeight { get; set; } = 0.5;
    public double RerankWeight { get; set; } = 0.5;
}

/// <summary>Typed settings for OpenAI / OIDC, bound from appsettings.json "OpenAi" section.</summary>
public sealed class OpenAiSettings
{
    public string EndpointUrl { get; set; } = "https://api.openai.com/v1";
    public string? ApiKey { get; set; }
    public string? Issuer { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scopes { get; set; }
}

/// <summary>Typed settings for Ollama local server, bound from appsettings.json "Ollama" section.</summary>
public sealed class OllamaSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int DefaultDimensions { get; set; } = 768;
    public string ChatModel { get; set; } = "llama3.2";
    public string VisionModel { get; set; } = "llava";
}

/// <summary>Typed settings for OpenTelemetry, bound from appsettings.json "OpenTelemetry" section.</summary>
public sealed class OpenTelemetrySettings
{
    public bool Enabled { get; set; } = false;
    public string ServiceName { get; set; } = "DocIngestor.Server";
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
    public string Protocol { get; set; } = "Grpc";
    public string? TenantId { get; set; }
}

