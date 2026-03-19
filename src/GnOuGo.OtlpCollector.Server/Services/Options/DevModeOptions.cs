namespace OtlpTenantCollector.Services.Options;

public sealed class DevModeOptions
{
    public const string SectionName = "DevMode";

    /// <summary>
    /// When enabled, tenant ID is optional everywhere (OTLP ingest + API queries).
    /// Data received without a tenant ID will have TenantId = null.
    /// </summary>
    public bool Enabled { get; set; }
}

