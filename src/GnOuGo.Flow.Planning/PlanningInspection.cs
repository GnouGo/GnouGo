using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningInspection
{
    internal static void Record(PlanningSession session, string phase, string state, string reason, string? requestId = null)
    {
        var events = session.InspectionEvents ??= [];
        var compact = session.Construction?.Compact;
        requestId ??= session.PendingCall?.Id;
        var previous = events.LastOrDefault();
        var occurrence = previous is not null && previous.Phase == phase && previous.RequestId == requestId
            ? previous.PhaseOccurrence : (previous?.PhaseOccurrence ?? 0) + 1;
        events.Add(new(events.Count == 0 ? 1 : events[^1].Sequence + 1, DateTimeOffset.UtcNow, phase, state, reason,
            requestId, compact?.CurrentBlockId, compact?.CurrentStep?.Id,
            session.Construction?.Checkpoints.Count, compact?.CurrentStep?.Objective ??
                compact?.Blocks?.FirstOrDefault(block => block.Id == compact.CurrentBlockId)?.Objective)
            { PhaseOccurrence = occurrence });
    }
}
