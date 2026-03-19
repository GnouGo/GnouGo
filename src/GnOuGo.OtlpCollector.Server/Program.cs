using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Data;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;
using OtlpTenantCollector.Web;

var builder = WebApplication.CreateSlimBuilder(args);

// Typed configuration binding
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<DevModeOptions>(builder.Configuration.GetSection(DevModeOptions.SectionName));

// Build AppOptions from typed options for backward compatibility
builder.Services.AddSingleton(sp =>
{
    var db        = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    var ingest    = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
    var retention = sp.GetRequiredService<IOptions<RetentionOptions>>().Value;
    var devMode   = sp.GetRequiredService<IOptions<DevModeOptions>>().Value;
    return AppOptions.FromOptions(db, ingest, retention, devMode);
});

// Ajouter Entity Framework Core avec SQLite
builder.Services.AddDbContext<TelemetryDbContext>((sp, options) =>
{
    var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    // Foreign Keys=False : les spans/logs peuvent arriver avant que le tenant existe (by design)
    options.UseSqlite($"Data Source={dbOptions.Path};Foreign Keys=False");
});

builder.Services.AddScoped<EfTelemetryStore>();
builder.Services.AddSingleton<TelemetryIngestQueue>();
builder.Services.AddSingleton<TelemetryEventBus>();
builder.Services.AddHostedService<TelemetryBatchWriter>();
builder.Services.AddHostedService<RetentionWorker>();

builder.Services.AddGrpc(o =>
{
    o.EnableDetailedErrors = false;
    o.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16 MiB
});

// Configurer Kestrel pour utiliser les URLs de appsettings.json
builder.WebHost.UseKestrel(options =>
{
    options.Configure(builder.Configuration.GetSection("Kestrel"));
});

// Static web UI
builder.Services.AddRouting();

var app = builder.Build();

// Initialiser la base de données avec Entity Framework Core
using (var scope = app.Services.CreateScope())
{
    var devModeOpts = scope.ServiceProvider.GetRequiredService<IOptions<DevModeOptions>>().Value;
    var efStore = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();

    await efStore.InitializeAsync(devMode: devModeOpts.Enabled);

    if (devModeOpts.Enabled)
    {
        app.Logger.LogInformation("[DevMode] Enabled — tenant ID is optional. Data without tenant will use null.");
    }
}

// --- gRPC OTLP receiver ---
app.MapGrpcService<OtlpTraceGrpcService>();
app.MapGrpcService<OtlpLogsGrpcService>();
app.MapGrpcService<OtlpMetricsGrpcService>();

// --- OTLP/HTTP receiver (protobuf only) ---
app.MapOtlpHttpReceiver();

// --- Multi-tenant API (tenants + trace exploration) ---
app.MapTenantApi();

// --- Admin/Debug endpoints ---
app.MapGet("/admin/queue-status", (TelemetryIngestQueue queue) =>
{
    var reader = queue.Channel.Reader;
    var writer = queue.Channel.Writer;
    return Results.Json(new
    {
        can_read = reader.CanCount,
        can_write = !writer.TryComplete(new Exception("test")), // Test sans vraiment fermer
        message = "Queue is active. Data will be flushed within FlushSeconds interval."
    });
});

app.MapGet("/admin/config", (AppOptions opt) =>
{
    return Results.Json(new
    {
        database_path = opt.DbPath,
        batch_size = opt.BatchSize,
        flush_seconds = opt.FlushSeconds,
        channel_capacity = opt.ChannelCapacity,
        retention_sweep_seconds = opt.RetentionSweepSeconds
    });
});

// --- Static UI ---
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
