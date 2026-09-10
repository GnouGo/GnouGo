using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OtlpTenantCollector.Hosting;
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
    public async Task DevelopmentHostStartupPreservesDatabaseUsedByAnotherHost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-collector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(directory, "telemetry.db"),
            ["DevMode:Enabled"] = "true"
        }).Build();
        try
        {
            await using var services = new ServiceCollection().AddLogging().AddOtlpCollectorCore(configuration).BuildServiceProvider();
            await services.InitializeOtlpCollectorAsync(TestContext.Current.CancellationToken);
            await using var runningHost = services.CreateAsyncScope();
            var first = runningHost.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            var tenant = Guid.NewGuid();
            await first.CreateTenantAsync(tenant, "retained", 60);
            // Keep the first host's SQLite connection open while the second starts.
            await runningHost.ServiceProvider.GetRequiredService<TelemetryDbContext>().Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await services.InitializeOtlpCollectorAsync(TestContext.Current.CancellationToken);
            await using var secondHost = services.CreateAsyncScope();
            var second = secondHost.ServiceProvider.GetRequiredService<TelemetryDbContext>();
            Assert.True(await second.Tenants.AnyAsync(t => t.Id == tenant && t.Name == "retained", TestContext.Current.CancellationToken));
            await first.CreateTenantAsync(Guid.NewGuid(), "still writable", 60);
            Assert.Equal(2, await second.Tenants.CountAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            using var pool = new SqliteConnection("Data Source=" + Path.Combine(directory, "telemetry.db"));
            SqliteConnection.ClearPool(pool);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetAllTenantsAsync_ReturnsTenantsOrderedByCreatedUtcDescending_OnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TelemetryDbContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

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

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = new EfTelemetryStore(db, NullLogger<EfTelemetryStore>.Instance);

        var tenants = await store.GetAllTenantsAsync();

        Assert.Collection(
            tenants,
            tenant => Assert.Equal(newerTenantId, tenant.Id),
            tenant => Assert.Equal(olderTenantId, tenant.Id));
    }
}


