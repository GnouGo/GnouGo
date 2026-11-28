namespace GnOuGo.Files.Server.Options;

public sealed class FilesServerOptions
{
    public const string SectionName = "Files";

    public Uri? PublicBaseUrl { get; set; }

    public string TenantId { get; set; } = "default";

    public double DefaultTtlHours { get; set; } = 12;

    public int PurgeIntervalSeconds { get; set; } = 60;

    public string? StorageRootPath { get; set; }

    public string? DatabasePath { get; set; }

    public TimeSpan GetDefaultTtl()
    {
        if (DefaultTtlHours <= 0)
            return TimeSpan.FromHours(12);

        return TimeSpan.FromHours(DefaultTtlHours);
    }

    public TimeSpan GetPurgeInterval()
    {
        if (PurgeIntervalSeconds <= 0)
            return TimeSpan.FromMinutes(1);

        return TimeSpan.FromSeconds(PurgeIntervalSeconds);
    }
}
