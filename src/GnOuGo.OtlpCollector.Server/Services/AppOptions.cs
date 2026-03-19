using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Services;

public sealed record AppOptions(
    string DbPath,
    int BatchSize,
    int FlushSeconds,
    int ChannelCapacity,
    int RetentionSweepSeconds,
    bool DevModeEnabled
)
{
    public static AppOptions FromOptions(
        DatabaseOptions db,
        IngestOptions ingest,
        RetentionOptions retention,
        DevModeOptions devMode) =>
        new(
            DbPath: db.Path,
            BatchSize: ingest.BatchSize,
            FlushSeconds: ingest.FlushSeconds,
            ChannelCapacity: ingest.ChannelCapacity,
            RetentionSweepSeconds: retention.SweepSeconds,
            DevModeEnabled: devMode.Enabled
        );
}
