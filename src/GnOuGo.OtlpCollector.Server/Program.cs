using Microsoft.Extensions.Options;
using OtlpTenantCollector.Hosting;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;
using OtlpTenantCollector.Web;

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

app.MapGet("/admin/queue-status", async httpContext =>
{
    var queue = httpContext.RequestServices.GetRequiredService<TelemetryIngestQueue>();
    var reader = queue.Channel.Reader;

    await OtlpApiResponses.ExecuteAsync(
        httpContext,
        OtlpApiResponses.Json(
            new QueueStatusResponse(
                CanCount: reader.CanCount,
                CanPeek: reader.CanPeek,
                ReaderCompleted: reader.Completion.IsCompleted,
                Message: "Queue is active. Data will be flushed within FlushSeconds interval."),
            OtlpApiJsonContext.Default.QueueStatusResponse));
});

app.MapGet("/admin/config", async httpContext =>
{
    var opt = httpContext.RequestServices.GetRequiredService<AppOptions>();

    await OtlpApiResponses.ExecuteAsync(
        httpContext,
        OtlpApiResponses.Json(
            new CollectorConfigResponse(
                DatabasePath: opt.DbPath,
                BatchSize: opt.BatchSize,
                FlushSeconds: opt.FlushSeconds,
                ChannelCapacity: opt.ChannelCapacity,
                RetentionSweepSeconds: opt.RetentionSweepSeconds,
                DevModeEnabled: opt.DevModeEnabled),
            OtlpApiJsonContext.Default.CollectorConfigResponse));
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
