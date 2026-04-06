using Microsoft.Extensions.Options;
using OtlpTenantCollector.Hosting;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});

builder.Services.AddOtlpCollectorCore(builder.Configuration);
builder.Services.AddRouting();

var app = builder.Build();

await app.Services.InitializeOtlpCollectorAsync();

using (var scope = app.Services.CreateScope())
{
    var devModeOpts = scope.ServiceProvider.GetRequiredService<IOptions<DevModeOptions>>().Value;
    if (devModeOpts.Enabled)
        app.Logger.LogInformation("[DevMode] Enabled — tenant ID is optional. Data without tenant will use null.");
}

app.MapOtlpCollectorApi();

app.MapGet("/admin/queue-status", (TelemetryIngestQueue queue) =>
{
    var reader = queue.Channel.Reader;
    var writer = queue.Channel.Writer;

    return Results.Json(new
    {
        can_count = reader.CanCount,
        can_peek = reader.CanPeek,
        reader_completed = reader.Completion.IsCompleted,
        message = "Queue is active. Data will be flushed within FlushSeconds interval."
    });
});

app.MapGet("/admin/config", (AppOptions opt) => Results.Json(new
{
    database_path = opt.DbPath,
    batch_size = opt.BatchSize,
    flush_seconds = opt.FlushSeconds,
    channel_capacity = opt.ChannelCapacity,
    retention_sweep_seconds = opt.RetentionSweepSeconds,
    dev_mode_enabled = opt.DevModeEnabled
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
