namespace GnOuGo.Files.Server.Options;

public sealed class FilesServerOptions
{
    public const string SectionName = "Files";

    public double DefaultTtlHours { get; set; } = 12;

    public int PurgeIntervalSeconds { get; set; } = 60;

    public string? StorageRootPath { get; set; }

    public string? DatabasePath { get; set; }

    public int StreamBufferSizeBytes { get; set; } = 1024 * 128;

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

    public int GetStreamBufferSize()
    {
        return StreamBufferSizeBytes < 81920 ? 81920 : StreamBufferSizeBytes;
    }
}

