using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;
using OtlpTenantCollector.Web;

namespace OtlpTenantCollector.Hosting;

public static class OtlpCollectorHostingExtensions
{
    public static IServiceCollection AddOtlpCollectorCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<IngestOptions>(configuration.GetSection(IngestOptions.SectionName));
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.Configure<DevModeOptions>(configuration.GetSection(DevModeOptions.SectionName));

        services.AddSingleton(sp =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var ingest = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
            var retention = sp.GetRequiredService<IOptions<RetentionOptions>>().Value;
            var devMode = sp.GetRequiredService<IOptions<DevModeOptions>>().Value;
            return AppOptions.FromOptions(db, ingest, retention, devMode);
        });

        services.AddDbContext<TelemetryDbContext>((sp, options) =>
        {
            var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var dbPath = dbOptions.Path;
            if (!Path.IsPathRooted(dbPath))
                dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);

            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            options.UseSqlite($"Data Source={dbPath};Foreign Keys=False");
        });

        services.AddScoped<EfTelemetryStore>();
        services.AddSingleton<TelemetryIngestQueue>();
        services.AddSingleton<TelemetryEventBus>();
        services.AddHostedService<TelemetryBatchWriter>();
        services.AddHostedService<RetentionWorker>();
        services.AddGrpc(o =>
        {
            o.EnableDetailedErrors = false;
            o.MaxReceiveMessageSize = 16 * 1024 * 1024;
        });

        return services;
    }

    public static async Task InitializeOtlpCollectorAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var devModeOpts = scope.ServiceProvider.GetRequiredService<IOptions<DevModeOptions>>().Value;
        var efStore = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
        await efStore.InitializeAsync(devMode: devModeOpts.Enabled);
    }

    public static IEndpointRouteBuilder MapOtlpCollectorApi(
        this WebApplication app,
        bool includeReceivers = true,
        bool includeHealthEndpoint = true)
    {
        if (includeReceivers)
        {
            app.MapGrpcService<OtlpTraceGrpcService>();
            app.MapGrpcService<OtlpLogsGrpcService>();
            app.MapGrpcService<OtlpMetricsGrpcService>();
            app.MapOtlpHttpReceiver(includeHealthEndpoint);
        }

        app.MapTenantApi();
        return app;
    }
}


