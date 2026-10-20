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
        state.Construction.Compact.PresentationVersion = 2;
        state.Construction.Compact.InterfaceExtractions ??= [];
        options["construction_presentation_version"] = 2;
        state.Construction.Limits.InputLimits ??= new();
        var limits = state.Construction.Limits.InputLimits;
        var general = state.Request.Generation.MaxInputTokensPerRequest;
        if (general <= 0 || limits.Selection <= 0 || limits.Draft <= 0 || limits.Review <= 0)
            throw new PlanningConflictException("Fresh compact input allowances must be positive.");
        limits.Selection = Math.Min(general, Math.Min(3_000, limits.Selection));
        limits.Draft = Math.Min(general, Math.Min(8_000, limits.Draft));
        limits.Review = Math.Min(general, Math.Min(4_000, limits.Review));
        options["construction_version"] = 2;
        options["construction_input_limits"] = System.Text.Json.JsonSerializer.SerializeToNode(
            state.Construction.Limits.InputLimits, PlanningJsonContext.Default.PlanningConstructionInputLimits);
        options.TryAdd("clarification_policy", 1);
        if (state.Request.Baseline is { } baseline) state.Plan = PlanningConstruction.Clone(baseline);
    }
}
