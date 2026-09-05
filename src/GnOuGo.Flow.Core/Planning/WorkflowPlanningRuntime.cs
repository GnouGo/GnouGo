using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Public adapter over the existing runtime's contract preparation and validators.</summary>
public sealed class WorkflowPlanningRuntime : IPlanningRuntime
{
    private readonly WorkflowPlanExecutor _executor = new();
    private readonly StepExecutionContext _context;
    private readonly Func<PlanningSnapshot, CancellationToken, Task>? _checkpoint;

    public WorkflowPlanningRuntime(WorkflowEngine engine, Func<PlanningSnapshot, CancellationToken, Task>? checkpoint = null)
    {
        _context = new StepExecutionContext
        {
            Engine = engine,
            Step = new CompiledStep { Source = new StepDef { Id = "typed_planning", Type = "workflow.plan", Input = new JsonObject() } },
            Data = new JsonObject { ["inputs"] = new JsonObject(), ["steps"] = new JsonObject() },
            Limits = engine.Limits,
            LLMUsageBudget = engine.LLMUsageBudget
        };
        _checkpoint = checkpoint;
    }

    public WorkflowPlanningRuntime(StepExecutionContext context) => _context = context;

    public Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct)
        => _executor.PrepareTypedContractsAsync(_context, request, ct);
    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
        => _context.CallLLMAsync(_context.Engine.LLMClient ?? throw new InvalidOperationException("No planning model is configured."), request, "workflow.plan.typed." + phase, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct)
        => _executor.ValidateTypedArtifactAsync(_context, yaml, request, preparation, ct);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct)
        => _executor.ValidateTypedScenariosAsync(yaml, preparation, ct);
    public Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) => _checkpoint?.Invoke(snapshot, ct) ?? Task.CompletedTask;
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct)
        => _executor.ValidateTypedCatalogAsync(_context.Engine, preparation, ct);
}
