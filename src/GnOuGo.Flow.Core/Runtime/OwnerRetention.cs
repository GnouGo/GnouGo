using System.Globalization;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Host configuration. Only newly created durable owners capture the default.</summary>
public sealed class OwnerRetentionPolicy
{
    public const int CurrentVersion = 2;
    public const string ConfigurationKey = "Retention:OperationalDays";
    public static OwnerRetentionPolicy Default { get; } = new(2);
    public int OperationalDays { get; }
    public TimeProvider Clock { get; }

    public OwnerRetentionPolicy(int operationalDays, TimeProvider? clock = null)
    {
        Clock = clock ?? TimeProvider.System;
        if (operationalDays < 1 || operationalDays > (DateTimeOffset.MaxValue - Clock.GetUtcNow()).Days)
            throw new ArgumentOutOfRangeException(nameof(operationalDays));
        OperationalDays = operationalDays;
    }

    public static OwnerRetentionPolicy Parse(string? value) => value is null ? Default :
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            ? new(days) : throw new ArgumentException("Retention:OperationalDays must be a positive whole number.");

    public OwnerRetention Capture()
    {
        var now = Clock.GetUtcNow().ToUniversalTime();
        return new(CurrentVersion, OperationalDays, OperationalDays, OwnerRetention.Active, now, now, null, null);
    }
}

/// <summary>Resume-relevant planning transitions. Bookkeeping and the storage revision do not advance the crash window.</summary>
public sealed record PlanningRetentionCheckpoint(string Status, string Phase, string? PendingCallId, int ModelCalls,
    int AnswerBatches, int ConstructionSequence, string? PlanHash, string? CandidateHash, int ReplanAttempts)
{
    public static PlanningRetentionCheckpoint From(PlanningSession session)
    {
        var last = session.Construction?.Checkpoints.LastOrDefault();
        return new(session.Status, session.Phase, session.PendingCall?.Id, session.ModelCalls,
            session.AnswerHistory?.Count ?? 0, last?.Sequence ?? 0, last?.PlanHash, session.Construction?.Candidate?.Hash,
            session.ReplanAttempts);
    }
}

/// <summary>Encrypted operational metadata. It grants no execution, replay, settlement or deletion authority.</summary>
public sealed record OwnerRetention(int PolicyVersion, int ResumeDays, int TerminalDays, string Phase,
    DateTimeOffset CheckpointUtc, DateTimeOffset EnteredUtc, DateTimeOffset? DeadlineUtc, string? WaitIdentity)
{
    public const string Active = "active", Waiting = "waiting", Terminal = "terminal", Reconciliation = "needs_reconciliation";

    [JsonIgnore]
    public DateTimeOffset CrashResumeUntilUtc => CheckpointUtc.AddDays(ResumeDays);

    [JsonIgnore]
    public bool IsValid => PolicyVersion is 1 or OwnerRetentionPolicy.CurrentVersion && ResumeDays > 0 && TerminalDays > 0 &&
        EnteredUtc.Offset == TimeSpan.Zero && CheckpointUtc.Offset == TimeSpan.Zero && EnteredUtc <= CheckpointUtc &&
        ResumeDays <= (DateTimeOffset.MaxValue - CheckpointUtc).Days && TerminalDays <= (DateTimeOffset.MaxValue - EnteredUtc).Days &&
        Phase is Active or Waiting or Reconciliation or Terminal &&
        (Phase == Waiting) == (WaitIdentity is not null) &&
        (DeadlineUtc is null || DeadlineUtc.Value.Offset == TimeSpan.Zero) &&
        DeadlineUtc == (Phase == Active ? null : EnteredUtc.AddDays(Phase == Terminal ? TerminalDays : ResumeDays));

    public bool IsDue(DateTimeOffset now) => PolicyVersion != OwnerRetentionPolicy.CurrentVersion || now >= (DeadlineUtc ?? CrashResumeUntilUtc);

    private OwnerRetention Advance(string phase, DateTimeOffset now, bool checkpoint, string? waitIdentity = null)
    {
        var changed = Phase != phase || WaitIdentity != waitIdentity;
        if (!changed && (!checkpoint || phase != Active)) return this;
        now = now.ToUniversalTime();
        if (now < CheckpointUtc) now = CheckpointUtc;
        var entered = changed ? now : EnteredUtc;
        return this with { Phase = phase, WaitIdentity = waitIdentity, CheckpointUtc = now, EnteredUtc = entered,
            DeadlineUtc = phase == Active ? null : entered.AddDays(phase == Terminal ? TerminalDays : ResumeDays) };
    }

    public OwnerRetention ForPlanning(PlanningSession session, DateTimeOffset now, bool checkpoint)
    {
        var phase = session.Status switch
        {
            PlanningStatus.Stopped or PlanningStatus.Failed or PlanningStatus.Cancelled
                when session.PendingCall is not null || session.Usage?.UncertainCalls > 0 ||
                    session.Diagnostics.Any(d => d.Code == "MODEL_DISPATCH_UNVERIFIABLE") => Reconciliation,
            PlanningStatus.Clarification or PlanningStatus.FinalReview or PlanningStatus.DiscoveryRecovery or PlanningStatus.Approved => Waiting,
            PlanningStatus.Saved or PlanningStatus.Stopped or PlanningStatus.Failed or PlanningStatus.Cancelled => Terminal,
            _ => Active
        };
        // WaitingSinceUtc also serves the coordinator's elapsed-time accounting and can be refreshed on a repeat command.
        var wait = session.Status switch
        {
            PlanningStatus.Clarification => FormattableString.Invariant($"questions/{session.AnswerHistory?.Count ?? 0}/") +
                Ids(session.GetQuestions().Select(q => q.Id)),
            PlanningStatus.DiscoveryRecovery => "discovery/" + Ids(session.Discovery.Failures?.Select(f => f.Id) ?? []),
            PlanningStatus.FinalReview => FormattableString.Invariant($"review/{session.ModelCalls}/{session.Construction?.Checkpoints.LastOrDefault()?.PlanHash}"),
            PlanningStatus.Approved => "handoff/" + session.ApprovedHash,
            _ => null
        };
        return Advance(phase, now, checkpoint, wait);
    }

    private static string Ids(IEnumerable<string> ids) => string.Concat(ids.Order(StringComparer.Ordinal)
        .Select(id => FormattableString.Invariant($"{id.Length}:{id}")));

    public OwnerRetention ForRun(WorkflowRun run, DateTimeOffset now, bool checkpoint, string? requestedInput = null)
    {
        var phase = run.Status switch
        {
            WorkflowRunStatus.NeedsReconciliation => Reconciliation,
            WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled
                when run.ModelUsage?.UncertainCalls > 0 || run.Invocations.Values.Any(i =>
                    i.Recovery == StepRecovery.External && i.DispatchedAt.HasValue && !i.CompletedAt.HasValue) => Reconciliation,
            WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled => Terminal,
            _ => Active
        };
        // A child human-input adapter can request input while the parent still owns the run.
        if (phase != Active) return Advance(phase, now, checkpoint);
        static bool IsWaiting(WorkflowInvocation input) =>
            input.Status == "waiting_for_human" && !input.Control.ContainsKey("human_response");
        string? pending = null;
        if (WaitIdentity is not null && run.Invocations.TryGetValue(WaitIdentity, out var current) && IsWaiting(current))
            pending = WaitIdentity;
        else if (requestedInput is not null)
            pending = requestedInput;
        else if (Phase == Waiting || run.Status == WorkflowRunStatus.WaitingForHuman)
            pending = run.Invocations.Values.Where(IsWaiting).Select(i => i.Id).Min(StringComparer.Ordinal);
        return pending is null ? Advance(Active, now, checkpoint) : Advance(Waiting, now, checkpoint, pending);
    }
}
