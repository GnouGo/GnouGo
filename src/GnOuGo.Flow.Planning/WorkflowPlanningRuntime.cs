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
            var preparation = await CapabilityPreparation.PrepareTypedContractsAsync(_context, request, snapshot, this, ct);
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
    public async Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct)
    {
        var unreserved = snapshot.Construction.PendingCalls.Where(c => c.ReasoningCapabilityFingerprint is null).ToArray();
        foreach (var call in unreserved)
        {
            var resolver = _context.Engine.LLMCapabilities ?? _context.Engine.LLMClient as ILLMCapabilityResolver ?? _model as ILLMCapabilityResolver;
            IReadOnlyList<string>? levels;
            try { levels = resolver is null ? null : await resolver.SupportedReasoningLevelsAsync(call.Request.Provider, call.Request.Model, ct); }
            catch (Exception error)
            {
                // Metadata lookup happens before the durable dispatch boundary.
                // Do not leave a reservation that a final checkpoint would try
                // to resolve again, or classify it as an unverifiable dispatch.
                NotDispatched();
                if (error is OperationCanceledException) throw;
                throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("MODEL_METADATA_UNAVAILABLE", "Model capability metadata could not be read. No planning request was dispatched.");
            }
            if (levels is null || call.Request.Reasoning is null || !levels.Contains(call.Request.Reasoning, StringComparer.Ordinal))
            {
                NotDispatched();
                throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("MODEL_REASONING_UNPROVEN", "The configured model metadata does not establish support for the phase reasoning level. No provider request was dispatched.");
            }
            call.ReasoningCapabilityFingerprint = PlanningGraphCompiler.Fingerprint(string.Join("\n", levels.Order(StringComparer.Ordinal)));

            void NotDispatched()
            {
                foreach (var pending in unreserved)
                {
                    snapshot.Construction.PendingCalls.Remove(pending);
                    if (snapshot.RequestAccounting.SingleOrDefault(a => a.Id == pending.Id) is { } reservation) reservation.Evidence = "not_dispatched";
                }
            }
        }
        await _checkpoint(snapshot, ct);
    }
}
