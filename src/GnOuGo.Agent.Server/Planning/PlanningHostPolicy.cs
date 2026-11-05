using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Authority is an encrypted host record bound to immutable creation settings, not an option string.</summary>
public sealed class PlanningHostPolicy(IKeyVaultRecordStore records, IOptions<TypedWorkflowPlanningSettings> settings,
    ApplicationLlmBudgetPolicy? budgetPolicy = null)
{
    private const string Collection = "agent-planning-host-authority-v1";
    internal const string CaptureOption = "agent_host_policy";

    internal async Task CaptureAsync(PlanningSession state, GnOuGo.AI.Core.LLMOptions options,
        string thresholdMode, PlanningDiagnosticGrant? grant, CancellationToken ct, PlanningSession? creation = null, bool applicationOwnsLimits = false)
    {
        if (thresholdMode is not ("strict" or "diagnostic")) throw new ArgumentException("Unknown threshold mode.");
        PlanningTokenThresholds.RejectReserved(state.Request.Options);
        if (thresholdMode == "diagnostic" && (!settings.Value.DiagnosticEnabled || grant is null))
            throw new UnauthorizedAccessException("Diagnostic planning requires server enablement and a local operator grant.");
        state.Construction ??= new();
        if (budgetPolicy is not null)
        {
            state.Request.Options[PlanningApplicationBudget.OptionName] = budgetPolicy.Capture();
            var capturedBudget = state.Request.Options[PlanningApplicationBudget.OptionName]!.AsObject();
            capturedBudget["applicationOwnsLimits"] = applicationOwnsLimits;
            if (capturedBudget["mode"]!.ToString() == "limited")
                capturedBudget["planningLimits"] = new JsonObject
                {
                    ["max_calls"] = settings.Value.Calls(state.Request.Options["construction_preset"]?.ToString() ?? "ordinary"),
                    ["max_total_tokens"] = settings.Value.MaxTotalTokens,
                    ["max_elapsed_ms"] = settings.Value.MaxActiveMilliseconds,
                    ["max_estimated_cost"] = new JsonObject { ["amount"] = budgetPolicy.PlanningAllowance.Amount, ["currency"] = budgetPolicy.PlanningAllowance.Currency }
                };
            if (applicationOwnsLimits && PlanningApplicationBudget.TrackingOnly(state.Request)) state.Request.Options.Remove("llm_budget");
            else if (!applicationOwnsLimits && state.Request.Options["llm_budget"] is not null)
                state.Request.Options[PlanningApplicationBudget.OptionName]!["explicitWorkflowLimits"] = true;
            else if (!applicationOwnsLimits && capturedBudget["mode"]!.ToString() == "limited")
                state.Request.Options["llm_budget"] = capturedBudget["planningLimits"]!.DeepClone();
        }
        var metadata = CapturedPlanningModel.Capture(state.Request, options);
        var deploymentCap = options.ResolveProvider(state.Request.Options["generator"]!["provider"]!.GetValue<string>())!.RequestPolicy.MaxOutputTokensCap;
        state.Request.Options["model_capacity"] = new JsonObject
        {
            ["maxInputTokens"] = metadata.MaxInputTokens, ["contextWindowTokens"] = metadata.ContextWindowTokens,
            ["maxOutputTokens"] = deploymentCap is > 0 ? Math.Min(metadata.MaxOutputTokens!.Value, deploymentCap.Value) : metadata.MaxOutputTokens,
            ["origin"] = "verified_model_and_configured_deployment"
        };
        if (thresholdMode == "diagnostic")
        {
            state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
            state.Request.Options["diagnostic_progress_policy"] = "advisory-v1";
            var modelMaximum = metadata.MaxOutputTokens!.Value;
            var cap = options.ResolveProvider(state.Request.Options["generator"]!["provider"]!.GetValue<string>())!.RequestPolicy.MaxOutputTokensCap;
            var effective = cap is > 0 ? Math.Min(modelMaximum, cap.Value) : modelMaximum;
            var phases = state.Construction!.Limits.PhasePolicy is { } configuredPhases
                ? JsonSerializer.SerializeToNode(configuredPhases, PlanningJsonContext.Default.PlanningConstructionPhasePolicy)!.Deserialize(PlanningJsonContext.Default.PlanningConstructionPhasePolicy)!
                : new PlanningConstructionPhasePolicy
                {
                    Navigation = new() { TargetInputTokens = 3000, MaxInputTokens = 6000, OutputTokens = state.Construction.Limits.DraftOutputTokens, Reasoning = state.Request.Generation.Reasoning },
                    Draft = new() { TargetInputTokens = 8000, MaxInputTokens = 16000, OutputTokens = state.Construction.Limits.DraftOutputTokens, Reasoning = state.Request.Generation.Reasoning },
                    Review = new() { TargetInputTokens = 4000, MaxInputTokens = 8000, OutputTokens = state.Construction.Limits.ReviewOutputTokens, Reasoning = state.Request.Generation.Reasoning }
                };
            foreach (var phase in new[] { phases.Navigation, phases.Draft, phases.Review })
            {
                if (settings.Value.DiagnosticModelMaximum) phase.OutputTokens = effective;
                if (phase.OutputTokens > effective) throw new PlanningConflictException("A requested phase output allocation exceeds the verified model/deployment maximum.");
                var physicalInput = Math.Min(metadata.MaxInputTokens!.Value, metadata.ContextWindowTokens!.Value - phase.OutputTokens);
                if (physicalInput <= 0) throw new PlanningConflictException("The output allocation leaves no verified input capacity.");
                // Preserve the ordinary configured threshold as an advisory target.
                // Physical capacity is independently enforced by captured capabilities.
                phase.MaxInputTokens = Math.Min(phase.MaxInputTokens, state.Request.Generation.MaxInputTokensPerRequest);
                phase.TargetInputTokens = Math.Min(phase.TargetInputTokens, phase.MaxInputTokens);
            }
            state.Request.Generation.MaxOutputTokens = new[] { phases.Navigation, phases.Draft, phases.Review }.Max(phase => phase.OutputTokens);
            state.Construction.Limits.DraftOutputTokens = phases.Draft.OutputTokens;
            state.Construction.Limits.ReviewOutputTokens = phases.Review.OutputTokens;
            state.Construction.Limits.PhasePolicy = phases;
            state.Request.Options["allocation_origin"] = new JsonObject
            {
                ["requested"] = settings.Value.DiagnosticModelMaximum ? "model_maximum" : "configured_phases",
                ["requestedOutputTokens"] = settings.Value.DiagnosticModelMaximum ? modelMaximum : null,
                ["modelMaximum"] = modelMaximum, ["deploymentMaximum"] = cap, ["effectiveMaximum"] = effective,
                ["restriction"] = cap < modelMaximum ? "configured_deployment_output_cap" : "documented_model_output_capacity"
            };
        }
        PlanningSessionCreation.Capture(state, creation);
        state.Request.Options["agent_generation_at_creation"] = JsonSerializer.SerializeToNode(state.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions);
        state.Request.Options[CaptureOption] = new JsonObject { ["version"] = 1, ["thresholdMode"] = thresholdMode };
        await records.UpsertAsync(Collection, state.Request.TenantId, state.Request.SessionId,
            CaptureDocument(state).ToJsonString(), EfPlanningSessionStore.Author, ct);
    }

    internal async Task VerifyAsync(PlanningSession state, CancellationToken ct)
    {
        if (state.Request.Options[CaptureOption] is null) { PlanningTokenThresholds.RejectReserved(state.Request.Options); return; }
        var saved = await records.GetAsync(Collection, state.Request.TenantId, state.Request.SessionId, EfPlanningSessionStore.Author, ct);
        if (saved is null || !JsonNode.DeepEquals(JsonNode.Parse(saved.Value), CaptureDocument(state)))
            throw new PlanningConflictException("The host authorization or captured planning settings cannot be verified. No request was dispatched.");
    }

    internal async Task VerifyCreationAsync(PlanningSession state, CancellationToken ct)
    {
        await VerifyAsync(state, ct);
        if (!JsonNode.DeepEquals(state.Request.Options["agent_generation_at_creation"],
            JsonSerializer.SerializeToNode(state.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions)))
            throw new PlanningConflictException("The initial host allocation capture changed before its first session checkpoint.");
    }

    private static JsonObject CaptureDocument(PlanningSession state)
    {
        var request = state.Request;
        var options = request.Options;
        var capture = new JsonObject { ["tenant"] = request.TenantId, ["session"] = request.SessionId,
            ["maxModelCalls"] = request.MaxModelCalls, ["maxReplanAttempts"] = request.MaxReplanAttempts,
            ["policy"] = JsonSerializer.SerializeToNode(request.Policy, PlanningJsonContext.Default.PlanningPolicy) };
        foreach (var key in new[] { CaptureOption, PlanningTokenThresholds.OptionName, PlanningTokenThresholds.OptimizationOptionName,
                     "diagnostic_progress_policy", "generator", "construction_limits", "llm_budget", PlanningApplicationBudget.OptionName, CapturedPlanningModel.OptionName, "allocation_origin", "agent_generation_at_creation", "model_capacity",
                     "automatic_correction", "clarification_policy", "construction_preset", "compilation_profile", "mapping_profile", "planning_validation_profile", "business_selection_contract", "host_save" })
            capture[key] = options[key]?.DeepClone();
        return capture;
    }
}

internal sealed class WorkflowHostPolicy(PlanningHostPolicy policy, GnOuGo.AI.Core.LLMOptions options,
    GnOuGo.Agent.Server.SmartFlow.SecureWorkflowRuntimeFactory runtimeFactory, PlanningDiagnosticGrant? grant = null) : IPlanningHostPolicy
{
    public Task CaptureAsync(PlanningSession session, CancellationToken ct) => policy.CaptureAsync(session, options, grant is null ? "strict" : "diagnostic", grant, ct);
    public Task CaptureAsync(PlanningSession session, PlanningSession creation, CancellationToken ct) => policy.CaptureAsync(session, options, grant is null ? "strict" : "diagnostic", grant, ct, creation);
    public Task VerifyAsync(PlanningSession session, CancellationToken ct) => policy.VerifyAsync(session, ct);
    public Task VerifyCreationAsync(PlanningSession session, CancellationToken ct) => policy.VerifyCreationAsync(session, ct);
    public async Task<IPlanningRuntimeModel?> CreateModelAsync(PlanningSession session, CancellationToken ct)
        => session.Request.Options[CapturedPlanningModel.OptionName] is null ? null : new RuntimeModel(await runtimeFactory.CreateAsync(ct, session.Request));
    private sealed class RuntimeModel(GnOuGo.Agent.Server.SmartFlow.SecureWorkflowRuntimeSession runtime) : IPlanningRuntimeModel
    {
        public GnOuGo.Flow.Core.Runtime.ILLMClient Client => runtime.LlmClient;
        public GnOuGo.Flow.Core.Runtime.ILLMCapabilityResolver? Capabilities => runtime.LlmCapabilityResolver;
        public GnOuGo.Flow.Core.Runtime.IModelUsageCostEstimator CostEstimator => new GnOuGo.Flow.Integrations.ModelMetadataUsageCostEstimator(runtime.Options);
        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }
}
