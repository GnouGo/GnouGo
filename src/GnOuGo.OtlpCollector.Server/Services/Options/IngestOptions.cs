namespace OtlpTenantCollector.Services.Options;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    public int BatchSize { get; set; } = 100;
    public int FlushSeconds { get; set; } = 5;
    public int ChannelCapacity { get; set; } = 20_000;
}

