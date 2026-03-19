namespace OtlpTenantCollector.Services.Options;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public int SweepSeconds { get; set; } = 60;
}

