using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Server.Configuration;
using GnOuGo.Flow.Server.HumanInput;
using GnOuGo.Flow.Server.Telemetry;

var builder = WebApplication.CreateSlimBuilder(args);

var runtimeAppSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (File.Exists(runtimeAppSettingsPath))
{
    builder.Configuration.AddJsonFile(runtimeAppSettingsPath, optional: true, reloadOnChange: false);

    var runtimeEnvironmentSettingsPath = Path.Combine(
        AppContext.BaseDirectory,
        $"appsettings.{builder.Environment.EnvironmentName}.json");
    if (File.Exists(runtimeEnvironmentSettingsPath))
    {
        builder.Configuration.AddJsonFile(runtimeEnvironmentSettingsPath, optional: true, reloadOnChange: false);
    }
}

// Runtime files supply defaults; deployment settings and explicit arguments win.
builder.Configuration.AddEnvironmentVariables();
if (args.Length > 0)
{
    builder.Configuration.AddCommandLine(args);
}

// ── Configuration (typed) ──
builder.Services.Configure<OpenTelemetrySettings>(
    builder.Configuration.GetSection(OpenTelemetrySettings.SectionName));

// ── OpenTelemetry (conditional) ──
var otelSettings = builder.Configuration
    .GetSection(OpenTelemetrySettings.SectionName)
    .Get<OpenTelemetrySettings>() ?? new OpenTelemetrySettings();

if (otelSettings.Enabled)
{
    var protocol = otelSettings.Protocol.Equals("HttpProtobuf", StringComparison.OrdinalIgnoreCase)
        ? OtlpExportProtocol.HttpProtobuf
        : OtlpExportProtocol.Grpc;

    var resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService(otelSettings.ServiceName, serviceVersion: "1.0.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["host.name"] = Environment.MachineName
        });

    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(resourceBuilder)
                .AddSource(OTelWorkflowTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                    o.Protocol = protocol;
                    if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                        o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                });
        })
        .WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(OTelWorkflowTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
                    o.Protocol = protocol;
                    if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                        o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
                });
        });

    // Export ILogger logs via OTLP (enables log correlation with traces via TraceId/SpanId)
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.SetResourceBuilder(resourceBuilder);
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.ParseStateValues = true;
        logging.AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otelSettings.OtlpEndpoint);
            o.Protocol = protocol;
            if (!string.IsNullOrWhiteSpace(otelSettings.TenantId))
                o.Headers = $"X-Tenant-Id={otelSettings.TenantId}";
        });
    });
}

// ── Services ──
if (otelSettings.Enabled)
{
    // Registered as singleton — will be resolved AFTER the host is built,
    // so TracerProvider/MeterProvider are already listening on AddSource/AddMeter.
    builder.Services.AddSingleton<IWorkflowTelemetry, OTelWorkflowTelemetry>();
}
else
{
    builder.Services.AddSingleton<IWorkflowTelemetry>(NullWorkflowTelemetry.Instance);
}

builder.Services.AddSingleton<ILLMClient>(sp =>
{
    var llmOptions = builder.Configuration.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();
    var http = new HttpClient { Timeout = LLMHttpClientDefaults.MinimumTimeout };
    var routingClient = new RoutingLLMClient(http, llmOptions);
    return new RoutingLLMClientAdapter(routingClient);
});
builder.Services.AddSingleton<IMcpClientFactory>(sp =>
{
    var llmOptions = builder.Configuration.GetSection(LLMOptions.SectionName).Get<LLMOptions>() ?? new LLMOptions();
    if (llmOptions.McpServers.Count > 0)
        return new ConfiguredMcpClientFactory(
            llmOptions.McpServers,
            sp.GetService<IHumanInputProvider>(),
            llmOptions.DefaultProvider,
            llmOptions.DefaultModel);
    return new InMemoryMcpClientFactory();
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ServerHumanInputProvider>();
builder.Services.AddSingleton<IHumanInputProvider>(sp => sp.GetRequiredService<ServerHumanInputProvider>());
builder.Services.AddSingleton<IWorkflowRunStore>(_ => EncryptedWorkflowRunStore.CreateWorkspace());

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();

// ── API Endpoints ──

app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));

app.MapPost("/api/workflow/run", async (
    HttpContext httpContext,
    WorkflowRunRequest request,
    IWorkflowTelemetry telemetry,
    ILLMClient llm,
    IMcpClientFactory mcpFactory,
    IMemoryCache mcpCache,
    IHumanInputProvider hitlProvider,
    IWorkflowRunStore runStore,
    ILoggerFactory loggerFactory) =>
{
    try
    {
        var prepared = PrepareWorkflowRun(request);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var logger = loggerFactory.CreateLogger("GnOuGo.Flow.WorkflowEngine");
        var runId = request.RunId ?? Guid.NewGuid().ToString("N");
        httpContext.Response.Headers["X-Workflow-Run-Id"] = runId;
        httpContext.Response.Headers["X-Workflow-Tenant-Id"] = string.IsNullOrWhiteSpace(otelSettings.TenantId) ? "default" : otelSettings.TenantId.Trim();
        var result = await ExecuteWorkflowAsync(prepared.Workflow, prepared.Inputs, telemetry, llm, mcpFactory, mcpCache, hitlProvider, logger, runId, cts.Token, runStore, otelSettings.TenantId);
        return Results.Ok(ToWorkflowRunResponse(result));
    }
    catch (WorkflowParseException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (WorkflowCompilationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (WorkflowRunConflictException ex) { return Results.Conflict(ex.Message); }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Commands use the configured host tenant; a caller cannot select another tenant's records.
var executionTenant = string.IsNullOrWhiteSpace(otelSettings.TenantId) ? "default" : otelSettings.TenantId.Trim();
var runs = app.MapGroup("/api/tenants/{tenantId}/runs");
runs.AddEndpointFilter(async (context, next) =>
    context.HttpContext.Request.RouteValues["tenantId"]?.ToString() == executionTenant
        ? await next(context) : Results.NotFound());
runs.MapGet("", async (string tenantId, IWorkflowRunStore store, CancellationToken ct) =>
    Results.Json((await store.ListAsync(tenantId, ct)).ToList(), WorkflowRunJsonContext.Default.ListWorkflowRun));
runs.MapGet("/{runId}", async (string tenantId, string runId, IWorkflowRunStore store, CancellationToken ct) =>
    await store.ReadAsync(tenantId, runId, ct) is { } run ? Results.Json(run, WorkflowRunJsonContext.Default.WorkflowRun) : Results.NotFound());
runs.MapPost("/{runId}/human-input", async (string tenantId, string runId, WorkflowHumanAnswer request,
    IWorkflowRunStore store, ServerHumanInputProvider human, CancellationToken ct) =>
{
    try
    {
        await WorkflowRunHumanResponses.RecordAsync(store, tenantId, runId, request.InvocationId, request.Response, ct);
        human.TrySubmitResponse(runId, request.InvocationId, request.Response);
        return Results.Ok();
    }
    catch (WorkflowRunConflictException ex) { return Results.Conflict(ex.Message); }
});
runs.MapPost("/{runId}/{command}", async (string tenantId, string runId, string command, WorkflowRunCommand request,
    IWorkflowRunStore store, IWorkflowTelemetry telemetry, ILLMClient llm, IMcpClientFactory mcpFactory,
    IMemoryCache cache, IHumanInputProvider human, ILoggerFactory loggers, CancellationToken ct) =>
{
    try
    {
        if (command == "cancel")
            return Results.Json(await store.CancelAsync(tenantId, runId, request.ExpectedRevision, ct), WorkflowRunJsonContext.Default.WorkflowRun);
        var run = await store.ReadAsync(tenantId, runId, ct);
        if (run is null) return Results.NotFound();
        var engine = CreateWorkflowEngine(telemetry, llm, mcpFactory, cache, human,
            loggers.CreateLogger("GnOuGo.Flow.WorkflowEngine"), runId, store, tenantId);
        if (command == "reconcile")
            return Results.Json(await engine.ReconcileAsync(tenantId, runId, request.ExpectedRevision,
                request.InvocationId ?? "", request.ConfirmedStoppedReason, ct), WorkflowRunJsonContext.Default.WorkflowRun);
        if (command != "resume") return Results.BadRequest("Unknown execution command.");
        if (string.IsNullOrWhiteSpace(run.WorkflowYaml)) return Results.Conflict("The stored execution source is unavailable.");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(run.WorkflowYaml));
        await engine.ResumeAsync(tenantId, runId, request.ExpectedRevision, document.Workflows[run.WorkflowName], ct);
        return Results.Json(await store.ReadAsync(tenantId, runId, ct), WorkflowRunJsonContext.Default.WorkflowRun);
    }
    catch (WorkflowRunConflictException ex) { return Results.Conflict(ex.Message); }
});

app.MapPost("/api/workflow/run/stream", async (
    HttpContext httpContext,
    WorkflowRunRequest request,
    IWorkflowTelemetry telemetry,
    ILLMClient llm,
    IMcpClientFactory mcpFactory,
    IMemoryCache mcpCache,
    IHumanInputProvider hitlProvider,
    IWorkflowRunStore runStore,
    ILoggerFactory loggerFactory,
    IOptions<JsonOptions> jsonOptions) =>
{
    PreparedWorkflowRun prepared;
    try
    {
        prepared = PrepareWorkflowRun(request);
    }
    catch (WorkflowParseException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
        return;
    }
    catch (WorkflowCompilationException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
        return;
    }
    catch (InvalidWorkflowRunRequestException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
        return;
    }

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";
    httpContext.Response.Headers.CacheControl = "no-store";
    httpContext.Response.Headers.Pragma = "no-cache";
    httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

    var channel = Channel.CreateUnbounded<WorkflowStreamEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    var streamingTelemetry = new StreamingWorkflowTelemetry(telemetry, evt => channel.Writer.TryWrite(evt));
    var logger = loggerFactory.CreateLogger("GnOuGo.Flow.WorkflowEngine");
    var runId = request.RunId ?? Guid.NewGuid().ToString("N");
    httpContext.Response.Headers["X-Workflow-Run-Id"] = runId;
        httpContext.Response.Headers["X-Workflow-Tenant-Id"] = string.IsNullOrWhiteSpace(otelSettings.TenantId) ? "default" : otelSettings.TenantId.Trim();
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, timeoutCts.Token);

    RunResult? runResult = null;
    Exception? executionError = null;

    var executionTask = Task.Run(async () =>
    {
        try
        {
            runResult = await ExecuteWorkflowAsync(prepared.Workflow, prepared.Inputs, streamingTelemetry, llm, mcpFactory, mcpCache, hitlProvider, logger, runId, linkedCts.Token, runStore, otelSettings.TenantId);
        }
        catch (Exception ex)
        {
            executionError = ex;
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }, linkedCts.Token);

    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(httpContext.RequestAborted))
            await WriteStreamEventAsync(httpContext.Response, evt, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);

        await executionTask;

        if (executionError != null)
        {
            await WriteStreamEventAsync(httpContext.Response,
                new WorkflowStreamEvent("workflow.result", new
                {
                    response = new WorkflowRunResponse
                    {
                        Success = false,
                        Error = new WorkflowErrorDto
                        {
                            Code = "INTERNAL_ERROR",
                            Message = executionError.Message,
                            Retryable = false
                        }
                    },
                    summary = streamingTelemetry.GetSummarySnapshot()
                }),
                jsonOptions.Value.SerializerOptions,
                httpContext.RequestAborted);
            return;
        }

        if (runResult != null)
        {
            await WriteStreamEventAsync(httpContext.Response,
                new WorkflowStreamEvent("workflow.result", new
                {
                    response = ToWorkflowRunResponse(runResult),
                    summary = streamingTelemetry.GetSummarySnapshot()
                }),
                jsonOptions.Value.SerializerOptions,
                httpContext.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        linkedCts.Cancel();
    }
});

// ── SPA static files ──
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

// ── Request / Response DTOs ──

static PreparedWorkflowRun PrepareWorkflowRun(WorkflowRunRequest request)
{
    var doc = WorkflowParser.Parse(request.Workflow);
    var compiler = new WorkflowCompiler();
    var compiled = compiler.Compile(doc);

    var entrypoint = compiled.Entrypoint;
    if (entrypoint == null || !compiled.Workflows.ContainsKey(entrypoint))
        throw new InvalidWorkflowRunRequestException("No entrypoint workflow found");

    JsonNode? inputs = null;
    if (!string.IsNullOrWhiteSpace(request.Inputs))
    {
        try
        {
            inputs = JsonNode.Parse(request.Inputs);
        }
        catch
        {
            throw new InvalidWorkflowRunRequestException("Invalid JSON inputs");
        }
    }

    var workflow = compiled.Workflows[entrypoint];
    return new PreparedWorkflowRun(workflow, WorkflowInputDefaults.Apply(workflow.Source, inputs));
}

static Task<RunResult> ExecuteWorkflowAsync(
    CompiledWorkflow workflow,
    JsonNode? inputs,
    IWorkflowTelemetry telemetry,
    ILLMClient llm,
    IMcpClientFactory mcpFactory,
    IMemoryCache mcpCache,
    IHumanInputProvider hitlProvider,
    ILogger logger,
    string? runId,
    CancellationToken ct,
    IWorkflowRunStore? runStore = null, string? tenantId = null)
{
    return CreateWorkflowEngine(telemetry, llm, mcpFactory, mcpCache, hitlProvider, logger, runId, runStore, tenantId)
        .ExecuteAsync(workflow, inputs, ct);
}

static WorkflowEngine CreateWorkflowEngine(IWorkflowTelemetry telemetry, ILLMClient llm, IMcpClientFactory mcpFactory,
    IMemoryCache mcpCache, IHumanInputProvider hitlProvider, ILogger logger, string? runId, IWorkflowRunStore? runStore, string? tenantId)
    => new WorkflowEngine
    {
        WorkflowPlanner = new GnOuGo.Flow.Planning.HybridWorkflowPlanner(),
        PlanningRuntimeFactory = GnOuGo.Flow.Integrations.Planning.WorkflowPlanningRuntimeFactory.CreateWorkspace(),
        LLMClient = llm,
        ModelUsageCostEstimator = new ModelMetadataUsageCostEstimator(),
        McpClientFactory = mcpFactory,
        McpCache = mcpCache,
        HumanInputProvider = hitlProvider,
        RunStore = runStore,
        Telemetry = telemetry,
        Logger = logger,
        Limits = new ExecutionLimits { LogStepContent = true, RunId = runId, TenantId = string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim() }
    };

static WorkflowRunResponse ToWorkflowRunResponse(RunResult result) => new()
{
    Success = result.Success,
    Outputs = result.Outputs,
    Error = result.Error != null ? new WorkflowErrorDto
    {
        Code = result.Error.Code,
        Message = result.Error.Message,
        Retryable = result.Error.Retryable
    } : null,
    Steps = result.StepResults.Select(s => new StepResultDto
    {
        StepId = s.StepId,
        StepType = s.StepType,
        Status = s.Status.ToString(),
        DurationMs = s.Duration.TotalMilliseconds,
        Error = s.Error?.Message
    }).ToList()
};

static async Task WriteStreamEventAsync(HttpResponse response, WorkflowStreamEvent evt, JsonSerializerOptions serializerOptions, CancellationToken ct)
{
    await response.WriteAsync(JsonSerializer.Serialize(evt, serializerOptions), ct);
    await response.WriteAsync("\n", ct);
    await response.Body.FlushAsync(ct);
}

file sealed record PreparedWorkflowRun(CompiledWorkflow Workflow, JsonNode? Inputs);

file sealed class InvalidWorkflowRunRequestException(string message) : Exception(message);

public sealed record WorkflowHumanAnswer(string InvocationId, JsonNode? Response);

public sealed class WorkflowRunRequest
{
    public string? RunId { get; set; }
    public string Workflow { get; set; } = "";
    public string? Inputs { get; set; }
}

public sealed class WorkflowRunResponse
{
    public bool Success { get; set; }
    public JsonNode? Outputs { get; set; }
    public WorkflowErrorDto? Error { get; set; }
    public List<StepResultDto> Steps { get; set; } = new();
}

public sealed class WorkflowErrorDto
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Retryable { get; set; }
}

public sealed class StepResultDto
{
    public string StepId { get; set; } = "";
    public string StepType { get; set; } = "";
    public string Status { get; set; } = "";
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}
