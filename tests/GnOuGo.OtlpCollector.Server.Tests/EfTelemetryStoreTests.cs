using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;
using Xunit;

namespace GnOuGo.OtlpCollector.Server.Tests;

public sealed class EfTelemetryStoreTests
{
    [Fact]
    public async Task GetAllTenantsAsync_ReturnsTenantsOrderedByCreatedUtcDescending_OnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TelemetryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var olderTenantId = Guid.NewGuid();
        var newerTenantId = Guid.NewGuid();

        db.Tenants.AddRange(
            new TenantEntity
            {
                Id = olderTenantId,
                Name = "older",
                RetentionMinutes = 60,
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            },
            new TenantEntity
            {
                Id = newerTenantId,
                Name = "newer",
                RetentionMinutes = 120,
                CreatedUtc = DateTimeOffset.UtcNow
            });

        await db.SaveChangesAsync();

        var store = new EfTelemetryStore(db, NullLogger<EfTelemetryStore>.Instance);

        var tenants = await store.GetAllTenantsAsync();

        Assert.Collection(
            tenants,
            tenant => Assert.Equal(newerTenantId, tenant.Id),
            tenant => Assert.Equal(olderTenantId, tenant.Id));
    }
}


