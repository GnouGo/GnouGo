﻿﻿﻿namespace DocIngestor.Cli.Configuration;

/// <summary>
/// Configuration racine de DocIngestor.
/// </summary>
public class DocIngestorConfig
{
    public ChunkingConfig Chunking { get; set; } = new();
    public EmbeddingConfig Embedding { get; set; } = new();
    public StoreConfig Store { get; set; } = new();
    public ImagesConfig Images { get; set; } = new();
    public DebugConfig Debug { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
}

public class ChunkingConfig
{
    public string Mode { get; set; } = "auto";
    public int MinTokens { get; set; } = 400;
    public int TargetTokens { get; set; } = 1200;
    public int MaxTokens { get; set; } = 2000;
    public int OverlapTokens { get; set; } = 400;
}

public class EmbeddingConfig
{
    public bool Enabled { get; set; } = true;
    public string DefaultModel { get; set; } = "hash-384";
}

public class StoreConfig
{
    public bool Enabled { get; set; } = false;
    public string DefaultStore { get; set; } = "sqlite";
    public string StoreDirectory { get; set; } = "./_store";
    public string DefaultCollection { get; set; } = "default";
}

public class ImagesConfig
{
    public bool Enabled { get; set; } = false;
    public bool LoadBytes { get; set; } = false;
    public OcrConfig Ocr { get; set; } = new();
}

public class OcrConfig
{
    public bool Enabled { get; set; } = false;
    public string Engine { get; set; } = "openai";
    public string Language { get; set; } = "eng";
    public int Dpi { get; set; } = 300;
}

public class DebugConfig
{
    public bool Enabled { get; set; } = false;
    public string OutputDirectory { get; set; } = "./_debug";
}

public class SearchConfig
{
    public int DefaultTopK { get; set; } = 5;
    public RerankerConfig Reranker { get; set; } = new();
}

public class RerankerConfig
{
    public bool Enabled { get; set; }
    public string DefaultType { get; set; } = "bm25";
    public double VectorWeight { get; set; } = 0.5;
    public double RerankWeight { get; set; } = 0.5;
}

/// <summary>
/// Configuration OpenAI (endpoint, clé API, OIDC).
/// </summary>
public class OpenAiConfig
{
    public string EndpointUrl { get; set; } = "https://api.openai.com/v1";
    public string? ApiKey { get; set; }
    public string? Issuer { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scopes { get; set; }
}

/// <summary>
/// Configuration Ollama (serveur local).
/// </summary>
public class OllamaConfig
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int DefaultDimensions { get; set; } = 768;
    public string ChatModel { get; set; } = "llama3.2";
    public string VisionModel { get; set; } = "llava";
}

/// <summary>
/// Configuration OpenTelemetry.
/// </summary>
public class OpenTelemetryConfig
{
    public bool Enabled { get; set; } = true;
    public string ServiceName { get; set; } = "DocIngestor.Cli";
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
    public string Protocol { get; set; } = "Grpc";
    public string? TenantId { get; set; }
}

/// <summary>
/// Configuration racine complète.
/// </summary>
public class AppSettings
{
    public DocIngestorConfig DocIngestor { get; set; } = new();
    public OpenAiConfig OpenAi { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public OpenTelemetryConfig OpenTelemetry { get; set; } = new();
}

