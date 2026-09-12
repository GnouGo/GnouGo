using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>An immutable index into an owned source, never a second copy of its contents.</summary>
public sealed record PlanningReference(string Id, string Owner, long SourceRevision, string SourceId,
    string SourceFingerprint, string Kind, int Start, int Length);

/// <summary>A bounded decision page. The exact request and its receipt remain in the model journal.</summary>
public sealed class PlanningDecisionPage
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public bool Correction { get; set; }
    public string Phase { get; set; } = "";
    public string WorkflowKey { get; set; } = "$plan";
    public string EvidenceFingerprint { get; set; } = "";
    public List<string> Decisions { get; set; } = [];
    public List<string> References { get; set; } = [];
    public string Status { get; set; } = "pending";
    public int EstimatedInputTokens { get; set; }
    public int InputTargetTokens { get; set; }
    public int EstimatedAnswerTokens { get; set; }
    public string? RequestId { get; set; }
    public JsonObject? Candidate { get; set; }
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
}

public sealed record PlanningDecisionCorrection(string DecisionId, string EvidenceFingerprint, string WorkflowKey, string Gate);
public sealed record PlanningObligation(string Id, List<string> EvidenceReferences, string Owner, string Kind, bool Required);
public sealed record PlanningObligationRelation(string Producer, string Consumer, string Role);
public sealed record PlanningClarification(string DecisionId, List<string> EvidenceReferences,
    JsonObject AnswerSchema, List<string> Obligations)
{
    public string Question { get; init; } = "";
    public List<PlanningClarificationChoice> Choices { get; init; } = [];
    public string? DependencyFingerprint { get; init; }
}
public sealed record PlanningClarificationChoice(string Id, string Label, bool Preferred,
    string? PreferredReason, List<string> EvidenceReferences);

/// <summary>Resolution evidence indexed into authoritative intent and contracts; never an executable graph.</summary>
public sealed class PlanningBusinessDecision
{
    public string Id { get; set; } = "";
    public string ObligationId { get; set; } = "";
    public string SubjectReference { get; set; } = "";
    public List<string> EvidenceReferences { get; set; } = [];
    public string DependencyFingerprint { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? ResolutionOrigin { get; set; }
    public string? SelectedChoiceId { get; set; }
    public string? CustomAnswer { get; set; }
    public string? CustomAnswerFingerprint { get; set; }
    public bool CompleteDomain { get; set; }
    /// <summary>Unknown unless established from a declared value or an exact typed answer. Omission differs from null.</summary>
    public string ValuePresence { get; set; } = "unknown";
    public List<PlanningBusinessAlternative> Alternatives { get; set; } = [];
    public List<PlanningBusinessConstraint> Constraints { get; set; } = [];
    public List<string> AffectedObligations { get; set; } = [];
    public List<string> ReportedEvents { get; set; } = [];
}
public sealed class PlanningBusinessAlternative
{
    public string Id { get; set; } = "";
    public string EvidenceReference { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> OperationIds { get; set; } = [];
    public List<string> ExclusionReferences { get; set; } = [];
    public string? ExclusionCode { get; set; }
}
/// <summary>A declared constraint. The source must belong to the current decision's evidence.</summary>
public sealed record PlanningBusinessConstraint(string Kind, string ChoiceId, string SourceReference,
    string Origin, string Applicability = "always");
public sealed record PlanningUnsupportedObligation(string ObligationId, string Code, List<string> EvidenceReferences);
public sealed record PlanningTechnicalStop(string Code, string Phase, string Location, bool Unverifiable = false);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PlanningValidWorkflow), "valid_workflow")]
[JsonDerivedType(typeof(PlanningNeedUserClarification), "need_user_clarification")]
[JsonDerivedType(typeof(PlanningUnsupported), "unsupported")]
public abstract record PlanningOutcome
{
    [JsonIgnore]
    public string Name => this switch
    {
        PlanningValidWorkflow => "valid_workflow",
        PlanningNeedUserClarification => "need_user_clarification",
        PlanningUnsupported => "unsupported",
        _ => throw new InvalidOperationException("Unknown planning outcome.")
    };
}
public sealed record PlanningValidWorkflow(string ArtifactHash) : PlanningOutcome;
public sealed record PlanningNeedUserClarification(PlanningClarification Decision) : PlanningOutcome;
public sealed record PlanningUnsupported(List<PlanningUnsupportedObligation> Obligations) : PlanningOutcome;

/// <summary>Effective reasoning is selected by the coordinator before reservation, never overridden by transport.</summary>
public sealed class PlanningReasoningProfile
{
    public string Routine { get; set; } = "low";
    public string Behavior { get; set; } = "medium";
    public string SemanticReview { get; set; } = "medium";
}
