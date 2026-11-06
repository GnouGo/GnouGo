using GnOuGo.Flow.Core.Planning;
using System.Diagnostics;

namespace GnOuGo.Flow.Planning;

internal static class PlanningInspection
{
    internal static void Record(PlanningSession session, string phase, string state, string reason, string? requestId = null,
        PlanningDiscoveryInspectionEvidence? discovery = null)
    {
        if (discovery is not null && discovery.SourceLabel is null)
            discovery = discovery with { SourceLabel = session.Discovery.Sources.SingleOrDefault(s => s.Id == discovery.SourceId)?.Description };
        var events = session.InspectionEvents ??= [];
        var compact = session.Construction?.Compact;
        requestId ??= session.PendingCall?.Id;
        var previous = events.LastOrDefault();
        var occurrence = discovery is null && previous is not null && previous.Discovery is null && previous.Phase == phase && previous.RequestId == requestId
            ? previous.PhaseOccurrence : (previous?.PhaseOccurrence ?? 0) + 1;
        events.Add(new(events.Count == 0 ? 1 : events[^1].Sequence + 1, DateTimeOffset.UtcNow, phase, state, reason,
            requestId, compact?.CurrentBlockId, compact?.CurrentStep?.Id,
            session.Construction?.Checkpoints.Count, compact?.CurrentStep?.Objective ??
                compact?.Blocks?.FirstOrDefault(block => block.Id == compact.CurrentBlockId)?.Objective)
            { PhaseOccurrence = occurrence, Discovery = discovery });
        if (discovery is not null)
            Activity.Current?.AddEvent(new ActivityEvent("planning.discovery", tags: new ActivityTagsCollection
            {
                ["gnougo.discovery.source_id"] = discovery.SourceId, ["gnougo.discovery.focus_id"] = discovery.FocusId,
                ["gnougo.discovery.outcome"] = discovery.Outcome, ["gnougo.discovery.snapshot_version"] = discovery.SnapshotVersion,
                ["gnougo.discovery.failure_id"] = discovery.FailureId, ["gnougo.planning.request_id"] = requestId
            }));
    }
}
