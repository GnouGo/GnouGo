namespace OtlpTenantCollector.Data;

/// <summary>
/// Historical EF Core context placeholder. Runtime storage is implemented by
/// <see cref="OtlpTenantCollector.Services.EfTelemetryStore"/> with Microsoft.Data.Sqlite
/// so published NativeAOT binaries avoid EF runtime dynamic-code paths.
/// </summary>
internal static class TelemetryDbContext
{
}
