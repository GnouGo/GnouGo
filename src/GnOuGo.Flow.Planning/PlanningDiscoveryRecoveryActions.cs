using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Metadata-only recovery of existing discovery evidence; never model receipt recovery.</summary>
public static class PlanningDiscoveryRecoveryActions
{
    public static bool Eligible(PlanningSession state) => state.SchemaVersion == 11 &&
        state.PendingCall is null && state.PendingQuestions is null &&
        state.Construction?.Compact?.CurrentStep is not null && state.Yaml is null && state.ApprovedHash is null &&
        state.ApprovalReview is null && state.SavedAgentId is null &&
        (state.Status == PlanningStatus.DiscoveryRecovery ||
         state.Status == PlanningStatus.Stopped && state.Diagnostics.Any(d => d.Code == "DISCOVERY_INCOMPLETE") ||
         state.Status == PlanningStatus.Generating && state.Phase is PlanningPhase.Discovery or PlanningCompactConstruction.DiscoveryPurpose &&
         state.Construction.Compact.CurrentStep is { Kind: null, PendingDiscovery: null, DiscoveryComplete: false } && Failures(state).Count > 0) &&
        state.Diagnostics.All(d => !d.Required || d.Code == "DISCOVERY_INCOMPLETE");

    public static IReadOnlyList<CapabilityDiscoveryFailure> Failures(PlanningSession state)
    {
        if (state.Construction?.Compact?.CurrentStep is not { } step) return [];
        var focuses = PlanningDiscoveryFocus.Choices(state);
        var failures = (state.Discovery.Failures ?? []).Where(f =>
            (f.StepId is null || f.StepId == step.Id) &&
            focuses.Any(focus => focus.Query == f.Query && (f.FocusId is null || focus.Id == f.FocusId)) &&
            !(state.Discovery.SupersededFailures?.Contains(f.Id, StringComparer.Ordinal) ?? false)).ToList();
        foreach (var page in state.Discovery.Pages.Where(p => p.UnavailableReason is not null && p.FailureId is null &&
                     focuses.Any(focus => focus.Query == (p.Query ?? step.Objective) && (p.FocusId is null || focus.Id == p.FocusId))))
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
        var retainedRecovery = step.PendingDiscovery?.Recovery;
        if (retainedRecovery is not null && retainedRecovery != selection)
            throw new PlanningConflictException("Finish the exact retained metadata recovery before selecting another resolution.");
        if (!state.Discovery.Sources.Any(s => s.Id == failure.SourceId) ||
            retainedRecovery is null && step.PendingDiscovery is { } pending && !pending.Requests.Any(r => r.SourceId == failure.SourceId))
            throw new PlanningConflictException("This failure does not belong to the retained discovery action.");
        await runtime.VerifyDiscoveryRecoveryAsync(state, ct);
        if (retainedRecovery is null)
        {
            step.DiscoveryProtocol = 2;
            state.Discovery.Failures ??= [];
            if (state.Discovery.Failures.All(f => f.Id != failure.Id)) state.Discovery.Failures.Add(failure);
            var old = step.PendingDiscovery;
            var remaining = old?.Pending.Where(r => r.SourceId != failure.SourceId).Select(r => r with
                { Cursor = null, Query = null, FocusId = MatchingFocus(state, r.Query, r.FocusId), Justification = r.Justification ?? "Explicit recovery of the retained metadata action." }).ToList() ?? [];
            if (selection.Resolution == "refresh_source")
            {
                remaining.Insert(0, new(failure.SourceId) { FocusId = MatchingFocus(state, failure.Query, failure.FocusId),
                    Justification = "Operator requested a fresh metadata snapshot after the retained source failure." });
            }
            // Original action/pages remain in immutable historical session revisions.
            step.PendingDiscovery = null;
            if (remaining.Count > 0)
            {
                var requests = remaining.DistinctBy(r => (r.SourceId, r.FocusId)).ToList();
                step.PendingDiscovery = new()
                {
                    Id = Guid.NewGuid().ToString("N"), Requests = requests, Recovery = selection,
                    ActionHash = HybridWorkflowPlanner.DiscoveryActionHash(step, requests),
                    Pending = requests.Select(r => r with { Query = PlanningDiscoveryFocus.Query(state, r.FocusId) }).ToList()
                };
                foreach (var request in step.PendingDiscovery.Pending)
                    (state.Discovery.ActiveSourceActions ??= new(StringComparer.Ordinal))[PlanningDiscoveryFocus.SourceKey(request.SourceId, request.Query!, request.FocusId)] = step.PendingDiscovery.Id;
            }
            state.Status = PlanningStatus.DiscoveryRecovery;
            state.Phase = PlanningCompactConstruction.DiscoveryPurpose;
            await runtime.CheckpointAsync(state, ct);
        }
        while (step.PendingDiscovery is not null)
            await HybridWorkflowPlanner.ContinueConstructionDiscoveryAsync(state, runtime, ct);
        if (selection.Resolution == "refresh_source") (state.Discovery.SupersededFailures ??= []).Add(failure.Id);
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

    private static string MatchingFocus(PlanningSession state, string? query, string? focusId) =>
        PlanningDiscoveryFocus.Choices(state).FirstOrDefault(f => f.Query == query && (focusId is null || f.Id == focusId))?.Id
        ?? throw new PlanningConflictException("The original discovery query has no retained focus; no new objective was invented.");
}
