using System.Diagnostics;
using System.Text.Json;
using GnOuGo.Assets.Animation.Server;
using GnOuGo.Observability.Core;

var builder = WebApplication.CreateSlimBuilder(args);
builder.AddGnOuGoOpenTelemetry(AnimationServerTelemetry.ActivitySourceName);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AnimationServerJsonContext.Default));
builder.Services.AddSingleton<SimulationPreparationService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Json(
    new ApiError("OK", "GnOuGo.Assets.Animation.Server is healthy."),
    AnimationServerJsonContext.Default.ApiError));

app.MapPost("/api/simulations/validate", (
    SimulationRequest request,
    HttpContext context,
    SimulationPreparationService service) =>
{
    using var activity = AnimationServerTelemetry.ActivitySource.StartActivity("animation.validate", ActivityKind.Server);
    AnimationServerTelemetry.ApplyTenant(activity, context);
    var response = service.Validate(request);
    var hardLimitExceeded = response.Diagnostics.Any(static diagnostic =>
        diagnostic.Code is "WORKFLOW_TOO_LARGE" or "INPUTS_TOO_LARGE");
    return Results.Json(
        response,
        AnimationServerJsonContext.Default.ValidationResponse,
        statusCode: hardLimitExceeded ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK);
});

app.MapPost("/api/simulations/stream", async (
    SimulationRequest request,
    HttpContext context,
    SimulationPreparationService service) =>
{
    using var activity = AnimationServerTelemetry.ActivitySource.StartActivity("animation.stream", ActivityKind.Server);
    AnimationServerTelemetry.ApplyTenant(activity, context);

    PreparedSimulation simulation;
    try
    {
        simulation = service.Prepare(request);
    }
    catch (SimulationRequestException exception)
    {
        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new ApiError(exception.Code, exception.Message, exception.Diagnostics),
            AnimationServerJsonContext.Default.ApiError,
            context.RequestAborted);
        return;
    }

    activity?.SetTag("animation.simulation_id", simulation.Metadata.SimulationId);
    activity?.SetTag("animation.seed", simulation.Metadata.Seed);
    activity?.SetTag("animation.scene", simulation.Metadata.Scene.ToString());

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "application/x-ndjson; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    var preparedEnvelope = new SimulationStreamEnvelope(
        "simulation.prepared",
        DateTimeOffset.UtcNow,
        Prepared: simulation.Metadata);
    await WriteEventAsync(context.Response, preparedEnvelope, context.RequestAborted);

    var stopwatch = Stopwatch.StartNew();
    try
    {
        foreach (var simulationEvent in simulation.Events)
        {
            var remaining = simulationEvent.OffsetMs - stopwatch.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), context.RequestAborted);
            await WriteEventAsync(
                context.Response,
                new SimulationStreamEnvelope(simulationEvent.Type, DateTimeOffset.UtcNow, Event: simulationEvent),
                context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        activity?.SetTag("animation.cancelled", true);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();

static async Task WriteEventAsync(HttpResponse response, SimulationStreamEnvelope envelope, CancellationToken cancellationToken)
{
    await response.WriteAsync(
        JsonSerializer.Serialize(envelope, AnimationServerJsonContext.Default.SimulationStreamEnvelope),
        cancellationToken);
    await response.WriteAsync("\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

public partial class Program;
