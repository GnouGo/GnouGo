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
        options.TryAdd("compilation_profile", TaskPlanCompiler.CompactProfile);
        options.TryAdd("mapping_profile", TaskPlanCompiler.AdaptiveMappingProfile);
        options.TryAdd("planning_validation_profile", TaskPlanCompiler.AssertionValidationProfile);
        options.TryAdd("planning_diagnostics_version", 1);
        options.TryAdd("planning_input_review_version", 2);
        options.TryAdd("business_judge_review_version", 2);
        state.Construction ??= new();
        state.Construction.Version = 2;
        state.Construction.Compact ??= new();
        state.Construction.Compact.PresentationVersion = 3;
        state.Construction.Compact.InterfaceExtractions ??= [];
        options["construction_presentation_version"] = 3;
        state.Construction.Limits.InputLimits ??= new();
        var limits = state.Construction.Limits.InputLimits;
        var general = state.Request.Generation.MaxInputTokensPerRequest;
        if (general <= 0 || limits.Selection <= 0 || limits.Draft <= 0 || limits.Review <= 0)
            throw new PlanningConflictException("Fresh compact input allowances must be positive.");
        var selectionCeiling = limits.Selection < 3_000 ? limits.Selection : int.MaxValue;
        var draftCeiling = limits.Draft < 8_000 ? limits.Draft : int.MaxValue;
        var reviewCeiling = limits.Review < 4_000 ? limits.Review : int.MaxValue;
        limits.Selection = Math.Min(general, Math.Min(3_000, limits.Selection));
        limits.Draft = Math.Min(general, Math.Min(8_000, limits.Draft));
        limits.Review = Math.Min(general, Math.Min(4_000, limits.Review));
        options["construction_version"] = 2;
        var policy = state.Construction.Limits.PhasePolicy ?? PlanningConstructionPhasePolicies.Read(state.Request) ??
            new PlanningConstructionPhasePolicy
            {
                Navigation = new() { TargetInputTokens = limits.Selection, MaxInputTokens = 6_000,
                    Reasoning = state.Request.Generation.Reasoning, OutputTokens = state.Construction.Limits.DraftOutputTokens },
                Draft = new() { TargetInputTokens = limits.Draft, MaxInputTokens = 16_000,
                    Reasoning = state.Request.Generation.Reasoning, OutputTokens = state.Construction.Limits.DraftOutputTokens },
                Review = new() { TargetInputTokens = limits.Review, MaxInputTokens = 8_000,
                    Reasoning = state.Request.Generation.Reasoning, OutputTokens = state.Construction.Limits.ReviewOutputTokens }
            };
        state.Construction.Limits.PhasePolicy = PlanningConstructionPhasePolicies.Capture(state.Request, policy);
        // Non-default tighter legacy host settings remain ceilings. Default
        // values become presentation targets; a captured profile can set both.
        void Clamp(PlanningPhaseSettings phase, int ceiling)
        {
            phase.MaxInputTokens = Math.Min(phase.MaxInputTokens, ceiling);
            phase.TargetInputTokens = Math.Min(phase.TargetInputTokens, phase.MaxInputTokens);
        }
        Clamp(state.Construction.Limits.PhasePolicy.Navigation, selectionCeiling);
        Clamp(state.Construction.Limits.PhasePolicy.Draft, draftCeiling);
        Clamp(state.Construction.Limits.PhasePolicy.Review, reviewCeiling);
        limits.Selection = state.Construction.Limits.PhasePolicy.Navigation.MaxInputTokens;
        limits.Draft = state.Construction.Limits.PhasePolicy.Draft.MaxInputTokens;
        limits.Review = state.Construction.Limits.PhasePolicy.Review.MaxInputTokens;
        options["construction_limits"] = System.Text.Json.JsonSerializer.SerializeToNode(
            state.Construction.Limits, PlanningJsonContext.Default.PlanningConstructionOptions);
        options["construction_input_limits"] = System.Text.Json.JsonSerializer.SerializeToNode(
            state.Construction.Limits.InputLimits, PlanningJsonContext.Default.PlanningConstructionInputLimits);
        options.TryAdd("clarification_policy", 1);
        if (state.Request.Baseline is { } baseline) state.Plan = PlanningConstruction.Clone(baseline);
    }
}
