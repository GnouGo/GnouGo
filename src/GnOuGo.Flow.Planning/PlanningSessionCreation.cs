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
        if (state.Request.Baseline is not null) return;
        options.TryAdd("planning_validation_profile", TaskPlanCompiler.AssertionValidationProfile);
        options.TryAdd("planning_diagnostics_version", 1);
        options.TryAdd("planning_input_review_version", 2);
        options.TryAdd("business_judge_review_version", 2);
    }
}
