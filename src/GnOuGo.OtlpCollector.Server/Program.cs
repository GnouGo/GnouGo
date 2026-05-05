using Microsoft.Extensions.Options;
using OtlpTenantCollector.Hosting;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;
using OtlpTenantCollector.Web;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
});

builder.WebHost.ConfigureKestrel((context, options) =>
{
    options.Configure(context.Configuration.GetSection("Kestrel"));
});

builder.Services.AddOtlpCollectorCore(builder.Configuration);
builder.Services.AddRouting();

var app = builder.Build();

app.Logger.LogInformation(
    "GnOuGo OTLP Collector starting. Environment={Environment}; ContentRoot={ContentRoot}; BaseDirectory={BaseDirectory}; Urls={Urls}",
    app.Environment.EnvironmentName,
    app.Environment.ContentRootPath,
    AppContext.BaseDirectory,
    builder.Configuration[WebHostDefaults.ServerUrlsKey] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "<not set>");

app.Logger.LogInformation(
    "Configured Kestrel endpoints: Grpc={GrpcUrl} ({GrpcProtocols}); Http={HttpUrl} ({HttpProtocols})",
    builder.Configuration["Kestrel:Endpoints:Grpc:Url"] ?? "<not set>",
    builder.Configuration["Kestrel:Endpoints:Grpc:Protocols"] ?? "<not set>",
    builder.Configuration["Kestrel:Endpoints:Http:Url"] ?? "<not set>",
    builder.Configuration["Kestrel:Endpoints:Http:Protocols"] ?? "<not set>");

app.Logger.LogInformation(
    "Configured collector storage: Database:Path={DatabasePath}; DevMode:Enabled={DevModeEnabled}",
    builder.Configuration["Database:Path"] ?? "<not set>",
    builder.Configuration["DevMode:Enabled"] ?? "<not set>");

await app.Services.InitializeOtlpCollectorAsync();

using (var scope = app.Services.CreateScope())
{
    var devModeOpts = scope.ServiceProvider.GetRequiredService<IOptions<DevModeOptions>>().Value;
    if (devModeOpts.Enabled)
        app.Logger.LogInformation("[DevMode] Enabled — tenant ID is optional. Data without tenant will use null.");
}

app.MapOtlpCollectorApi(includeHealthEndpoint: false);

app.MapGet("/health", async httpContext =>
{
    await OtlpApiResponses.ExecuteAsync(
        httpContext,
        OtlpApiResponses.Json(
            new HealthStatusResponse("ok"),
            OtlpApiJsonContext.Default.HealthStatusResponse));
});

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
