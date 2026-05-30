using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;
using OtlpTenantCollector.Services.Routing;
using OtlpTenantCollector.Web;
using GnOuGo.Workspace;

namespace OtlpTenantCollector.Hosting;

public static class OtlpCollectorHostingExtensions
{
    public static string ResolveDatabasePath(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return configuredPath;

        var defaultPath = new DatabaseOptions().Path;
        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? defaultPath
            : configuredPath.Replace('\\', '/').Trim();

        if (string.Equals(normalized, defaultPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "data/gnougo-telemetry.db", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(normalized);
            return Path.Combine(
                GnOuGoWorkspace.ResolveDesktopDirectory(),
                "GnOuGo",
                "data",
                fileName);
        }

        return Path.Combine(baseDirectory, configuredPath!);
    }

    public static IServiceCollection AddOtlpCollectorCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<IngestOptions>(configuration.GetSection(IngestOptions.SectionName));
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
        services.Configure<DevModeOptions>(configuration.GetSection(DevModeOptions.SectionName));
        services.Configure<TelemetryRoutingOptions>(configuration.GetSection(TelemetryRoutingOptions.SectionName));

        services.AddSingleton(sp =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var ingest = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
            var retention = sp.GetRequiredService<IOptions<RetentionOptions>>().Value;
            var devMode = sp.GetRequiredService<IOptions<DevModeOptions>>().Value;
            return AppOptions.FromOptions(db, ingest, retention, devMode, AppContext.BaseDirectory);
        });

        services.AddDbContext<TelemetryDbContext>((sp, options) =>
        {
            var appOpt = sp.GetRequiredService<AppOptions>();
            var dir = Path.GetDirectoryName(appOpt.DbPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            options.UseSqlite($"Data Source={appOpt.DbPath}");
        });

        services.AddScoped<EfTelemetryStore>();
        services.AddSingleton<TelemetryIngestQueue>();
        services.AddSingleton<TelemetryEventBus>();
        services.AddSingleton<TelemetryRouteClassifier>();
        services.AddHttpClient(nameof(OtlpHttpTelemetryForwarder));
        services.AddSingleton<OtlpHttpTelemetryForwarder>();
        services.AddSingleton<ITelemetryRouter, OptionsTelemetryRouter>();
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

    public static IEndpointRouteBuilder MapOtlpGrpcReceivers(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGrpcService<OtlpTraceGrpcService>();
        endpoints.MapGrpcService<OtlpLogsGrpcService>();
        endpoints.MapGrpcService<OtlpMetricsGrpcService>();
        return endpoints;
    }

    public static IEndpointRouteBuilder MapOtlpCollectorApi(
        this IEndpointRouteBuilder endpoints,
        bool includeReceivers = true,
        bool includeHealthEndpoint = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (includeReceivers)
        {
            endpoints.MapOtlpGrpcReceivers();
            endpoints.MapOtlpHttpReceiver(includeHealthEndpoint);
        }

        endpoints.MapTenantApi();
        return endpoints;
    }
}


