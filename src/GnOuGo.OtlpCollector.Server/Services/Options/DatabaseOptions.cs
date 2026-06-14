namespace OtlpTenantCollector.Services.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = ".GnOuGo/data/telemetry.db";
}
