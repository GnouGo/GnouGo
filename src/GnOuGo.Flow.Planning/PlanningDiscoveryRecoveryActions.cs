using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Metadata-only recovery of existing discovery evidence; never model receipt recovery.</summary>
public static class PlanningDiscoveryRecoveryActions
{
    public static bool Eligible(PlanningSession state) => state.SchemaVersion == 11 &&
        state.Status is PlanningStatus.Stopped or PlanningStatus.DiscoveryRecovery && state.PendingCall is null &&
        state.Construction?.Compact?.CurrentStep is not null && state.Yaml is null && state.ApprovedHash is null &&
        state.ApprovalReview is null && state.SavedAgentId is null &&
        (state.Status == PlanningStatus.DiscoveryRecovery || state.Diagnostics.Any(d => d.Code == "DISCOVERY_INCOMPLETE")) &&
        state.Diagnostics.All(d => !d.Required || d.Code == "DISCOVERY_INCOMPLETE");

    public static IReadOnlyList<CapabilityDiscoveryFailure> Failures(PlanningSession state)
    {
        var failures = (state.Discovery.Failures ?? []).Where(f =>
            !(state.Discovery.SupersededFailures?.Contains(f.Id, StringComparer.Ordinal) ?? false)).ToList();
        foreach (var page in state.Discovery.Pages.Where(p => p.UnavailableReason is not null && p.FailureId is null))
        {
            var id = "historical_" + PlanningGraphCompiler.Fingerprint(string.Join("\n", state.Request.SessionId,
                page.SourceId, page.Query, page.Cursor, page.UnavailableReason));
            if (state.Discovery.SupersededFailures?.Contains(id, StringComparer.Ordinal) == true || failures.Any(f => f.Id == id)) continue;
            failures.Add(new(id, page.SourceId, page.FocusId, page.Query ?? state.Construction!.Compact!.CurrentStep!.Objective,
                page.Cursor, "historical_unavailable", "Historical source failure; original technical cause unavailable.", page.SnapshotVersion,
                state.RequestMeasurements?.LastOrDefault()?.RequestId, null, state.UpdatedAtUtc) { StepId = state.Construction?.Compact?.CurrentStep?.Id });
        }
        return failures;
    }

    internal static async Task RecoverAsync(PlanningSession state, PlanningCommand command, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (!Eligible(state) || command.DiscoveryRecovery is not { } selection ||
            selection.Resolution is not ("continue_available" or "refresh_source"))
            throw new PlanningConflictException("Recovery requires an exact eligible discovery failure and resolution.");
        var failure = Failures(state).SingleOrDefault(f => f.Id == selection.FailureId)
            ?? throw new PlanningConflictException("The discovery failure does not belong to this session.");
        var step = state.Construction!.Compact!.CurrentStep!;
        if (!state.Discovery.Sources.Any(s => s.Id == failure.SourceId) ||
            step.PendingDiscovery is { } pending && !pending.Requests.Any(r => r.SourceId == failure.SourceId))
            throw new PlanningConflictException("This failure does not belong to the retained discovery action.");
        await runtime.VerifyDiscoveryRecoveryAsync(state, ct);
        step.DiscoveryProtocol = 2;
        state.Discovery.Failures ??= [];
        if (state.Discovery.Failures.All(f => f.Id != failure.Id)) state.Discovery.Failures.Add(failure);
        var old = step.PendingDiscovery;
        var remaining = old?.Pending.Where(r => r.SourceId != failure.SourceId).Select(r => r with
            { Cursor = null, Query = null, FocusId = MatchingFocus(state, r.Query), Justification = r.Justification ?? "Explicit recovery of the retained metadata action." }).ToList() ?? [];
        if (selection.Resolution == "refresh_source")
        {
            (state.Discovery.SupersededFailures ??= []).Add(failure.Id);
            remaining.Insert(0, new(failure.SourceId) { FocusId = MatchingFocus(state, failure.Query),
                Justification = "Operator requested a fresh metadata snapshot after the retained source failure." });
        }
        // Original action/pages remain in immutable historical session revisions.
        step.PendingDiscovery = null;
        if (remaining.Count > 0)
        {
            var requests = remaining.DistinctBy(r => (r.SourceId, r.FocusId)).ToList();
            step.PendingDiscovery = new()
            {
                Id = Guid.NewGuid().ToString("N"), Requests = requests,
                ActionHash = HybridWorkflowPlanner.DiscoveryActionHash(step, requests),
                Pending = requests.Select(r => r with { Query = PlanningDiscoveryFocus.Query(state, r.FocusId) }).ToList()
            };
            foreach (var request in step.PendingDiscovery.Pending)
                (state.Discovery.ActiveSourceActions ??= new(StringComparer.Ordinal))[PlanningDiscoveryFocus.SourceKey(request.SourceId, request.Query!, request.FocusId)] = step.PendingDiscovery.Id;
            await runtime.CheckpointAsync(state, ct);
            while (step.PendingDiscovery is not null)
                await HybridWorkflowPlanner.ContinueConstructionDiscoveryAsync(state, runtime, ct);
        }
        state.Diagnostics.RemoveAll(d => d.Code == "DISCOVERY_INCOMPLETE");
        state.Status = PlanningStatus.DiscoveryRecovery;
        state.Phase = PlanningCompactConstruction.DiscoveryPurpose;
        PlanningCompactConstruction.RemoveStaleDiscoveryDirectories(state);
        PlanningInspection.Record(state, state.Phase, "waiting", "Discovery recovery retained; explicit resume is required before further inference.",
            failure.RequestId, new(failure.SourceId, failure.FocusId, failure.Justification, failure.SnapshotVersion, failure.Cursor,
                selection.Resolution, failure.Id));
    }

    internal static async Task ResumeAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (!Eligible(state) || state.Status != PlanningStatus.DiscoveryRecovery || state.Construction!.Compact!.CurrentStep!.PendingDiscovery is not null)
            throw new PlanningConflictException("The recovered discovery session is not ready for explicit resume.");
        await runtime.VerifyDiscoveryRecoveryAsync(state, ct);
        state.Status = PlanningStatus.Generating;
        state.Phase = PlanningCompactConstruction.DiscoveryPurpose;
    }

    private static string MatchingFocus(PlanningSession state, string? query) =>
        PlanningDiscoveryFocus.Choices(state).FirstOrDefault(f => f.Query == query)?.Id
        ?? throw new PlanningConflictException("The original discovery query has no retained focus; no new objective was invented.");
}
