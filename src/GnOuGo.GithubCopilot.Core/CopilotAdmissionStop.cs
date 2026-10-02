namespace GnOuGo.GithubCopilot.Core;

public enum CopilotAdmissionStopKind { Tokens, Calls, Deadline }

/// <summary>Safe admission facts; charged tokens remain conservative, non-refundable reservations.</summary>
public sealed record CopilotAdmissionStop(CopilotAdmissionStopKind Kind, int MaxModelCalls, long MaxTotalTokens,
    DateTimeOffset Deadline, int ModelCalls, long ChargedTokens, long? RequiredInputTokens);

/// <summary>Observed interruption, not a completion receipt. Raw provider errors are deliberately omitted.</summary>
public sealed class CopilotSendInterruptedException(CopilotSendResult snapshot, bool budgetAdmissionObserved, bool verifiedContinuation = false)
    : InvalidOperationException("The Copilot turn was interrupted; inspect its execution observations.")
{
    public bool VerifiedContinuation { get; } = verifiedContinuation;
    public CopilotSendResult Snapshot { get; } = snapshot;
    public bool BudgetAdmissionObserved { get; } = budgetAdmissionObserved;
}
