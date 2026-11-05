using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Captured targets retain their values for measurement. Diagnostic authority
// changes admission, never the meaning of a contract or an execution limit.
internal static class PlanningOptimizationThresholds
{
    internal static bool RepeatsAdvisory(PlanningSession state) =>
        PlanningTokenThresholds.OptimizationAdvisory(state.Request) && state.Request.Options["diagnostic_progress_policy"]?.ToString() == "advisory-v1";

    internal static void RejectedRepeat(PlanningSession state, string code, JsonNode? details = null)
    {
        var measurement = state.RequestMeasurements?.LastOrDefault();
        Add(state, new("repeated_strategy", 2, 1, "captured_diagnostic_progress_policy", measurement?.Phase ?? state.Phase)
        {
            SessionId = state.Request.SessionId, RequestId = measurement?.RequestId,
            BlockId = state.Construction?.Compact?.CurrentBlockId, StepId = state.Construction?.Compact?.CurrentStep?.Id,
            Details = new JsonObject { ["rejection"] = code, ["evidence"] = details?.DeepClone() }
        });
        PlanningInspection.Record(state, measurement?.Phase ?? state.Phase, "warning",
            "Repeated generation/evidence was rejected without accepting invalid or unchanged work. Generation may continue within the authorized financial and transport controls.", measurement?.RequestId);
    }
    internal static int? Maximum(PlanningSession state, int target) =>
        PlanningTokenThresholds.OptimizationAdvisory(state.Request) ? null : target;

    internal static bool Exceeded(PlanningSession state, string stage, string kind, long current, long target)
    {
        if (current <= target) return false;
        if (!PlanningTokenThresholds.OptimizationAdvisory(state.Request)) return true;
        Record(state, stage, kind, current, target);
        return false;
    }

    internal static void Record(PlanningSession state, string stage, string kind, long current, long target,
        JsonObject? details = null)
    {
        var warning = new PlanningThresholdWarning(kind, current, target, "captured_optimization_target", stage)
        {
            SessionId = state.Request.SessionId,
            RequestId = state.PendingCall?.Id,
            StepId = state.Construction?.Compact?.CurrentStep?.Id,
            BlockId = state.Construction?.Compact?.CurrentBlockId,
            Checkpoint = state.Construction?.Checkpoints.Count,
            Details = details?.DeepClone().AsObject()
        };
        Add(state, warning);
    }

    internal static void Add(PlanningSession state, PlanningThresholdWarning warning)
    {
        var warnings = state.ThresholdWarnings ??= [];
        if (!warnings.Any(existing => existing with { Details = null } == warning with { Details = null } &&
            JsonNode.DeepEquals(existing.Details, warning.Details)))
            warnings.Add(warning);
    }
}
