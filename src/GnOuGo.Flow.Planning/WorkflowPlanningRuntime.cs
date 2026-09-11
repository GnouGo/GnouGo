using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning;

/// <summary>Provider-neutral adapter over engine transport, contracts, budgets, and validators.</summary>
public sealed class WorkflowPlanningRuntime : IPlanningRuntime
{
    private readonly StepExecutionContext _context;
    private readonly Func<PlanningSnapshot, CancellationToken, Task> _checkpoint;
    private readonly ILLMClient? _model;

    public WorkflowPlanningRuntime(WorkflowEngine engine, Func<PlanningSnapshot, CancellationToken, Task> checkpoint)
    {
        _context = new StepExecutionContext
        {
            Engine = engine,
            Step = new CompiledStep { Source = new StepDef { Id = "planning", Type = "workflow.plan", Input = new JsonObject() } },
            Data = new JsonObject { ["inputs"] = new JsonObject(), ["steps"] = new JsonObject() },
            Limits = engine.Limits,
            LLMUsageBudget = engine.LLMUsageBudget
        };
        _checkpoint = checkpoint;
    }
    public WorkflowPlanningRuntime(StepExecutionContext context, ILLMClient model, Func<PlanningSnapshot, CancellationToken, Task> checkpoint)
    {
        _context = context;
        _model = model;
        _checkpoint = checkpoint;
    }

    public async Task<PlanningPreparationProgress> PrepareAsync(PlanningSnapshot snapshot, CancellationToken ct)
    {
        var request = PlanningContext.EffectiveRequest(snapshot);
        var checkpoint = snapshot.PreparationCheckpoint ??= new() { Fingerprint = PlanningGraphCompiler.Fingerprint(request.Prompt) };
        var previousGeneration = _context.PlanningGeneration;
        _context.PlanningGeneration = request.Generation;
        _context.PreparationCheckpoint = checkpoint;
        _context.PersistPreparation = token => CheckpointAsync(snapshot, token);
        _context.PlanningModelDispatcher = (model, phase, token) => PlanningModelCalls.CallAsync(snapshot, this, phase, model, token);
        try
        {
            var preparation = await CapabilityPreparation.PrepareTypedContractsAsync(_context, request, ct);
            checkpoint.Stage = "completed"; checkpoint.Diagnostics.Clear();
            return new(checkpoint, preparation);
        }
        finally
        {
            _context.PlanningGeneration = previousGeneration;
            _context.PreparationCheckpoint = null; _context.PersistPreparation = null; _context.PlanningModelDispatcher = null;
        }
    }

    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
        => _context.CallModelAsync(_model ?? _context.Engine.LLMClient ?? throw new InvalidOperationException("No planning model is configured."), request, "workflow.plan." + phase, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct)
        => PlanningArtifactValidation.ValidateTypedArtifactAsync(_context, request.Yaml, request.Request, request.Preparation, ct, request.Bindings);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct)
        => PlanningArtifactValidation.ValidateTypedScenariosAsync(request.Yaml, request.Preparation, ct, request.Inputs, request.LoopItemSchemas, request.Observations);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct)
        => PlanningArtifactValidation.ValidateTypedCatalogAsync(_context.Engine, preparation, ct);
    public Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) => _checkpoint(snapshot, ct);
}
