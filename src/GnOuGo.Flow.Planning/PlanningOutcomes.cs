using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningOutcomes
{
    internal static void Refresh(PlanningSnapshot state)
    {
        if (state.Status == PlanningStatus.Unsupported && state.Outcome is not PlanningUnsupported)
            state.Status = PlanningStatus.Stopped; // A model assertion is not a proof of unsupportedness.
        if (state.Status is PlanningStatus.Stopped or PlanningStatus.Failed or PlanningStatus.Cancelled)
        {
            state.Outcome = null;
            var finding = state.Diagnostics.LastOrDefault(d => d.Required);
            state.TechnicalStop = new(finding?.Code ?? (state.Status == PlanningStatus.Cancelled ? "CANCELLED" : "PLANNING_STOPPED"),
                PlanningPhase.Resolve(state), finding?.Location ?? "$", state.RequestAccounting.Any(r => r.Evidence == "unverifiable"));
        }
        else
        {
            state.TechnicalStop = null;
            if (state.Outcome is PlanningValidWorkflow && (state.Status is not (PlanningStatus.Approved or PlanningStatus.Saved or PlanningStatus.Saving) ||
                state.ArtifactHash is null || state.ApprovedHash != state.ArtifactHash)) state.Outcome = null;
        }
    }
}
