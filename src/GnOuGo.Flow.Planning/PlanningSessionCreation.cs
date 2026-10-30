using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Creation defaults must be captured before a host's first persisted checkpoint.
// Rehydrated sessions, including revision zero, retain their original options.
internal static class PlanningSessionCreation
{
    internal static void Capture(PlanningSession state, PlanningSession? original = null)
    {
        if (!(original ?? state).IsNewlyConstructed || state.Revision != 0 || state.PendingCall is not null ||
            state.Plan is not null || state.ModelCalls != 0) return;
        var options = state.Request.Options;
        PlanningTransportPolicies.Capture(state.Request);
        options.TryAdd("compilation_profile", TaskPlanCompiler.CompactProfile);
        options.TryAdd("mapping_profile", TaskPlanCompiler.AdaptiveMappingProfile);
        options.TryAdd("planning_validation_profile", TaskPlanCompiler.AssertionValidationProfile);
        state.Construction ??= new();
        state.Construction.Compact ??= new();
        state.Construction.Compact.Blocks ??= [];
        options.TryAdd("business_selection_contract", 1);
        state.Construction.Limits.MaxNonProductiveModelCalls ??= 6;
        var policy = state.Construction.Limits.PhasePolicy ?? PlanningConstructionPhasePolicies.Read(state.Request) ??
            new PlanningConstructionPhasePolicy
            {
                Navigation = new() { TargetInputTokens = 3_000, MaxInputTokens = 6_000,
                    Reasoning = state.Request.Generation.Reasoning, OutputTokens = state.Construction.Limits.DraftOutputTokens },
                Draft = new() { TargetInputTokens = 8_000, MaxInputTokens = 16_000,
                    Reasoning = state.Request.Generation.Reasoning, OutputTokens = state.Construction.Limits.DraftOutputTokens },
                Review = new() { TargetInputTokens = 4_000, MaxInputTokens = 8_000,
                    Reasoning = state.Request.Generation.Reasoning, OutputTokens = state.Construction.Limits.ReviewOutputTokens }
            };
        state.Construction.Limits.PhasePolicy = PlanningConstructionPhasePolicies.Capture(state.Request, policy);
        options["construction_limits"] = System.Text.Json.JsonSerializer.SerializeToNode(
            state.Construction.Limits, PlanningJsonContext.Default.PlanningConstructionOptions);
        if (state.Request.Baseline is { } baseline) state.Plan = PlanningConstruction.Clone(baseline);
    }
}
