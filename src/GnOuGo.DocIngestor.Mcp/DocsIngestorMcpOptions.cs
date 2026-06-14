namespace GnOuGo.DocIngestor.Mcp;

public sealed class DocsIngestorMcpOptions
{
    public string DatabasePath { get; set; } = ".GnOuGo/data/gnougo-docs-ingestor-mcp.db";
    public string VectorDatabasePath { get; set; } = ".GnOuGo/data/gnougo-docs-ingestor-vectors.sqlite";
    public string OriginalsDirectory { get; set; } = ".GnOuGo/data/docs-ingestor/originals";
    public string DefaultCollection { get; set; } = "default";
    public string DefaultEmbeddingConfigName { get; set; } = string.Empty;
    public string DefaultTenantId { get; set; } = "default";
    public string DefaultAuthor { get; set; } = "docs-ingestor-mcp";
    public int DownloadTimeoutSeconds { get; set; } = 300;
    public long MaxDownloadBytes { get; set; } = 512L * 1024L * 1024L;
    public ChunkingOptions Chunking { get; set; } = new();
    public ImagesOptions Images { get; set; } = new();
}

public sealed class ChunkingOptions
{
    public string Mode { get; set; } = "recursive";
    public int MinTokens { get; set; } = 200;
    public int TargetTokens { get; set; } = 600;
    public int MaxTokens { get; set; } = 900;
    public int OverlapTokens { get; set; } = 60;
}

public sealed class ImagesOptions
{
    public bool EnableImageDiscovery { get; set; }
    public bool LoadImageBytes { get; set; }
    public bool EnableOcr { get; set; }
    public string OcrLanguage { get; set; } = "eng";
    public int OcrDpi { get; set; } = 300;
}
