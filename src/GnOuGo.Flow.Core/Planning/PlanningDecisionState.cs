using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>An immutable index into an owned source, never a second copy of its contents.</summary>
public sealed record PlanningReference(string Id, string Owner, long SourceRevision, string SourceId,
    string SourceFingerprint, string Kind, int Start, int Length)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningBaselineOwnership? Baseline { get; init; }
}

/// <summary>Derived structural provenance. Coordinates resolve against the authoritative baseline,
/// not a copied contract or a second graph. A field denotes an owner-bound prose annotation.</summary>
public sealed record PlanningBaselineOwnership(int Version, string Fingerprint, string OwnerKind,
    string? Workflow, string? Node, string? Direction, string? Port, string? Field);

/// <summary>A bounded decision page. The exact request and its receipt remain in the model journal.</summary>
public enum PlanningDecisionPageOrigin { Unknown, Initial, SemanticCorrection, OutputPartition, OutputBudgetEscalation }

/// <summary>One transport allowance for a canonical semantic decision and unchanged evidence.</summary>
public sealed record PlanningOutputBudgetEscalation(string ParentPageId, string ParentRequestId, string ParentRequestHash,
    string ParentReceiptFingerprint, string DecisionId, string CanonicalDecisionId, string EvidenceFingerprint, int Level = 1);

public sealed class PlanningDecisionPage
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public PlanningDecisionPageOrigin Origin { get; set; }
    public string? Gate { get; set; }
    public List<string> PartitionChildren { get; set; } = [];
    public string? OutputEscalationChildId { get; set; }
    public PlanningOutputBudgetEscalation? OutputBudgetEscalation { get; set; }
    public int? EffectiveOutputTokens { get; set; }
    /// <summary>Whether assignments are already restricted to a semantic correction, including its output partitions.</summary>
    public bool Correction { get; set; }
    public string Phase { get; set; } = "";
    public string WorkflowKey { get; set; } = "$plan";
    public string EvidenceFingerprint { get; set; } = "";
    public List<string> Decisions { get; set; } = [];
    /// <summary>Original semantic decisions sharing a coupled correction's finite allowances; coordinator-owned lineage.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SourceDecisionIds { get; set; }
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
public sealed record PlanningObligation(string Id, List<string> EvidenceReferences, string Owner, string Kind, bool Required)
{
    public PlanningSourceGrounding? Grounding { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningOperationAdmission? OperationAdmission { get; init; }
    public string? AdjudicationFingerprint { get; set; }
    public string Disposition { get; set; } = "preliminary";
    public List<string> PolicyIds { get; set; } = [];
}

/// <summary>One owned clause's contribution to a canonical action. Text remains in source references.</summary>
public sealed record PlanningOperationAssignment(string DecisionId, string ClauseReference, string ActionReference,
    string Kind, PlanningOperationNecessity Necessity, string? TargetId, string? BaselineReference)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeEvidenceId { get; init; }
    public string? Disposition { get; init; }
    public string? ResolutionOrigin { get; init; }
    public PlanningOperationEffectProof? Effect { get; init; }
    public string? EffectId { get; init; }
}

/// <summary>Business effect ownership and an explicit execution boundary; not an executable node.</summary>
public sealed record PlanningOperationEffectAnchor(string WorkflowScope, string OwnerReference,
    string BoundaryKind, string BoundaryReference, string? IterationReference = null)
{
    public PlanningOccurrenceBoundaryProof? OccurrenceProof { get; init; }
}

/// <summary>Owned semantic evidence for a separately requested execution, not an operation identity.</summary>
public sealed record PlanningOccurrenceBoundaryEvidence(string Kind, string OwnerReference, string BoundaryReference);
/// <summary>Validated occurrence ownership. Stored with the existing effect proof, never as a second graph.</summary>
public sealed record PlanningOccurrenceBoundaryProof(int Version, string RuntimeEvidenceId, string WorkflowScope,
    string? ScopeReference, PlanningOccurrenceBoundaryEvidence Evidence, string? DecisionId, string Fingerprint);

/// <summary>Producer-declared meaning of a complete policy source. Coordinates refer to unchanged instructions.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanningDeclaredPolicyEvidence(int Version, string SourceFingerprint, List<PlanningDeclaredPolicyClause> Clauses);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanningDeclaredPolicyClause(int Start, int Length, List<PlanningDeclaredPolicyMeaning> Meanings);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanningDeclaredPolicyMeaning(string Kind, bool Required);

/// <summary>Reference-only grounding retained on operation evidence. Candidates are a bounded identity domain,
/// never a second operation graph. Inputs, results and governing evidence do not enter occurrence identity.</summary>
public sealed record PlanningOperationEffectProof(int Version, string DecisionId, string Contribution,
    List<PlanningOperationEffectAnchor> Candidates, List<string> Inputs, List<string> Outputs,
    List<string> Producers, List<string> EvidenceReferences, string Origin, string EvidenceFingerprint);

/// <summary>Source-owned execution evidence, independent of preliminary obligation labels.</summary>
public sealed record PlanningRuntimeEvidence(string Id, string SourceReference, string ClauseReference, string Role,
    string? ActionReference, string? ResourceReference, string? ExecutionReference, string? Kind,
    string? EvidenceRole, string? BaselineReference, string? ResourceAction, PlanningOperationNecessity Necessity,
    string ProofFingerprint)
{
    public PlanningRuntimeExecutionScope ExecutionScope { get; init; }
    public PlanningRuntimeEvidenceOrigin Origin { get; init; }
    public string? ResourceOwnership { get; init; }
    public string? NecessityReference { get; init; }
    public PlanningOccurrenceBoundaryEvidence? OccurrenceBoundary { get; init; }
}
/// <summary>Evidence about capability necessity, independent of occurrence identity and runtime conditions.</summary>
public enum PlanningOperationNecessity { Unknown, Unspecified, Required, Optional }
public enum PlanningRuntimeExecutionScope { Unknown, PlanningArtifact, PublicContract, Policy, GeneratedWorkflow }
public enum PlanningRuntimeEvidenceOrigin { Unknown, SourceInterpretation, EngineSourceAuthority, EngineBaseline }

/// <summary>Canonical operation proof; preliminary source labels confer no execution authority.</summary>
public sealed record PlanningOperationAdmission(int Version, string CanonicalId, string AnchorReference,
    string? BaselineReference, List<PlanningOperationAssignment> Assignments, string EvidenceFingerprint,
    string ProofFingerprint)
{
    public PlanningOperationDependencyProof? Dependencies { get; init; }
}

/// <summary>Operation dataflow only. Evidence and decision lineage are independent of page packing.</summary>
public sealed record PlanningOperationDependencyProof(int Version, string DomainFingerprint,
    List<PlanningOperationDependencyAssignment> Assignments, string ProofFingerprint);
public sealed record PlanningOperationDependencyAssignment(string Producer, string Consumer, string Disposition,
    PlanningDependencyOrigin Origin, List<string> EvidenceReferences, string? DecisionId);
public enum PlanningDependencyOrigin { Unknown, DeterministicBaseline, DeterministicInterface, ModelSemanticSelection }

/// <summary>Assigned by the coordinator from owned source metadata, never selected by a model.</summary>
public enum PlanningSourceAuthority { Unknown, RequestedBehavior, ExistingBehavior, ConstraintsOnly }
public enum PlanningSourceSemanticRole { Unknown, RequestedAction, ExistingAction, PolicyConstraint, RuntimeCondition, Declaration }
public sealed record PlanningSourceGrounding(PlanningSourceAuthority Authority, PlanningSourceSemanticRole Role,
    string ClauseReference, string? BaselineReference, string Fingerprint)
{
    public string? DeclaredPolicyFingerprint { get; init; }
}
public sealed record PlanningObligationRelation(string Producer, string Consumer, string Role);

/// <summary>A reference-only adjudication of a preliminary declaration or modifier.</summary>
public sealed record PlanningDeclarationAssignment(string CandidateId, string Disposition,
    string? TargetId, string? NameReference, string? WorkflowScope, string Presence,
    string? DefaultReference)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DeclarationReference { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PresenceReference { get; init; }
}

/// <summary>Canonical public declaration. Text, defaults and contracts remain in their owned sources.</summary>
public sealed record PlanningBusinessDeclaration(string Id, string Direction, string WorkflowScope,
    string NameReference, string? BaselineReference, bool Required, string? DefaultReference,
    List<string> Candidates, List<string> Aliases, List<string> ModifierReferences,
    List<string> ClauseReferences, string ProofFingerprint);
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
