using Microsoft.EntityFrameworkCore;

namespace OtlpTenantCollector.Data;

internal static class TelemetryDatabaseBootstrap
{
    public static async Task EnsureInitializedAsync(TelemetryDbContext db, bool resetSchema, CancellationToken ct = default)
    {
        if (resetSchema)
            await db.Database.EnsureDeletedAsync(ct);

        await db.Database.EnsureCreatedAsync(ct);
    }
}
