using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>Host dependencies for the same engine used by initial chat execution and recovery.</summary>
public sealed class WorkflowRunService(IWorkflowRunStore store, SecureWorkflowRuntimeFactory runtimeFactory,
    AgentHumanInputProvider human, AgentOTelTelemetry telemetry, IMemoryCache cache, IServiceScopeFactory scopes,
    IWorkflowCandidateProvider candidates, IOptions<OpenTelemetrySettings> settings, ILogger<WorkflowRunService> logger)
{
    public string TenantId { get; } = WorkflowExecutionTenant.Resolve(settings);
    public Task<WorkflowRun?> ReadAsync(string id, CancellationToken ct) => store.ReadAsync(TenantId, id, ct);
    public Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken ct) => store.ListAsync(TenantId, ct);

    public async Task<WorkflowRun> CommandAsync(string id, string command, WorkflowRunCommand request, CancellationToken ct)
    {
        if (command == "cancel") return await store.CancelAsync(TenantId, id, request.ExpectedRevision, ct);
        var run = await ReadAsync(id, ct) ?? throw new KeyNotFoundException();
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var engine = new WorkflowEngine
        {
            RunStore = store, LLMClient = runtime.LlmClient, LLMCapabilities = runtime.LlmCapabilityResolver,
            ModelUsageCostEstimator = new ModelMetadataUsageCostEstimator(runtime.Options),
            McpClientFactory = runtime.McpClientFactory, McpCache = cache, HumanInputProvider = human,
            WorkflowCallResolver = new AgentDatabaseWorkflowCallResolver(scopes), WorkflowCandidateProvider = candidates,
            WorkflowPlanner = new GnOuGo.Flow.Planning.HybridWorkflowPlanner(),
            PlanningRuntimeFactory = GnOuGo.Flow.Integrations.Planning.WorkflowPlanningRuntimeFactory.CreateWorkspace(),
            PlanningPolicy = Planning.AgentPlanningPolicy.Create(),
            Telemetry = telemetry, Logger = logger,
            LlmDefaults = new() { Provider = runtime.Options.DefaultProvider, Model = runtime.Options.DefaultModel }
        };
        if (command == "reconcile")
            return await engine.ReconcileAsync(TenantId, id, request.ExpectedRevision, request.InvocationId ?? "", request.ConfirmedStoppedReason, ct);
        if (command != "resume") throw new ArgumentException("Unknown execution command.");
        if (string.IsNullOrWhiteSpace(run.WorkflowYaml)) throw new WorkflowRunConflictException("The stored execution source is unavailable.");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(run.WorkflowYaml));
        await engine.ResumeAsync(TenantId, id, request.ExpectedRevision, document.Workflows[run.WorkflowName], ct);
        return (await ReadAsync(id, ct))!;
    }
}

internal static class WorkflowRunEndpoints
{
    public static void MapWorkflowRunEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants/{tenantId}/runs");
        group.AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.RouteValues["tenantId"]?.ToString() == context.HttpContext.RequestServices.GetRequiredService<WorkflowRunService>().TenantId
                ? await next(context) : Results.NotFound());
        group.MapGet("", async (WorkflowRunService service, CancellationToken ct) =>
            Results.Json((await service.ListAsync(ct)).ToList(), WorkflowRunJsonContext.Default.ListWorkflowRun));
        group.MapGet("/{id}", async (string id, WorkflowRunService service, CancellationToken ct) =>
            await service.ReadAsync(id, ct) is { } run ? Results.Json(run, WorkflowRunJsonContext.Default.WorkflowRun) : Results.NotFound());
        group.MapPost("/{id}/{command}", async (string id, string command, WorkflowRunCommand request, WorkflowRunService service, CancellationToken ct) =>
        {
            try { return Results.Json(await service.CommandAsync(id, command, request, ct), WorkflowRunJsonContext.Default.WorkflowRun); }
            catch (WorkflowRunConflictException ex) { return Results.Conflict(ex.Message); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        });
    }
}
