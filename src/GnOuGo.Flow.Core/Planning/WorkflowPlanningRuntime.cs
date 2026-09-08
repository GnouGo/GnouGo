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
    {
        _context.PlanningGeneration = request.Generation;
        return _executor.PrepareTypedContractsAsync(_context, request, ct);
    }
    public async Task<PlanningPreparationProgress> AdvancePreparationAsync(PlanningRequest request, PlanningPreparationCheckpoint checkpoint,
        Func<CancellationToken, Task> persist, CancellationToken ct)
    {
        _context.PreparationCheckpoint = checkpoint; _context.PersistPreparation = persist;
        try
        {
            var preparation = await PrepareAsync(request, ct);
            checkpoint.Stage = "completed"; checkpoint.Diagnostics.Clear();
            return new(checkpoint, preparation);
        }
        finally { _context.PreparationCheckpoint = null; _context.PersistPreparation = null; }
    }

    public Task EnrichPreparationAsync(PlanningPreparation preparation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        WorkflowPlanExecutor.EnrichTypedPreparation(preparation);
        var changed = false;
        foreach (var (name, current) in _context.Engine.Registry.GetContracts())
        {
            if (preparation.StepContracts[name] is not JsonObject previous || previous["input"] is not JsonObject input ||
                !JsonNode.DeepEquals(previous["output"], current.OutputSchema) || JsonNode.DeepEquals(input, current.InputSchema) ||
                !IsOptionalInputExtension(input, current.InputSchema)) continue;
            previous["input"] = current.InputSchema.DeepClone(); changed = true;
        }
        if (changed) preparation.Fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(preparation.Fingerprint + preparation.StepContracts.ToJsonString())));
        return Task.CompletedTask;
    }

    private static bool IsOptionalInputExtension(JsonObject previous, JsonObject current)
    {
        if (previous["properties"] is not JsonObject before || current["properties"] is not JsonObject after) return false;
        if (before.Any(p => !after.ContainsKey(p.Key) || !JsonNode.DeepEquals(p.Value, after[p.Key]))) return false;
        var required = (current["required"] as JsonArray ?? []).Select(p => p?.ToString()).ToHashSet(StringComparer.Ordinal);
        if (after.Any(p => !before.ContainsKey(p.Key) && required.Contains(p.Key))) return false;
        var oldRest = previous.DeepClone().AsObject(); var newRest = current.DeepClone().AsObject(); oldRest.Remove("properties"); newRest.Remove("properties");
        return JsonNode.DeepEquals(oldRest, newRest);
    }
    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
        => _context.CallLLMAsync(_context.Engine.LLMClient ?? throw new InvalidOperationException("No planning model is configured."), request, "workflow.plan.typed." + phase, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct)
        => _executor.ValidateTypedArtifactAsync(_context, yaml, request, preparation, ct);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct)
        => _executor.ValidateTypedScenariosAsync(yaml, preparation, ct);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, JsonObject inputs, CancellationToken ct)
        => _executor.ValidateTypedScenariosAsync(yaml, preparation, ct, inputs);
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, JsonObject inputs, JsonObject loopItemSchemas, CancellationToken ct)
        => _executor.ValidateTypedScenariosAsync(yaml, preparation, ct, inputs, loopItemSchemas);
    public Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) => _checkpoint?.Invoke(snapshot, ct) ?? Task.CompletedTask;
    public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, JsonObject inputs, JsonObject loopItemSchemas, JsonObject observations, CancellationToken ct)
        => _executor.ValidateTypedScenariosAsync(yaml, preparation, ct, inputs, loopItemSchemas, observations);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct)
        => _executor.ValidateTypedCatalogAsync(_context.Engine, preparation, ct);
}
