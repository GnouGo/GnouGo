using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Source, immediate-step and continuation metadata; never an executable plan.</summary>
public sealed class PlanningCompactConstructionState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningConstructionBlock>? Blocks { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentBlockId { get; set; }
    public List<PlanningConstructionSource> Sources { get; set; } = [];
    public Dictionary<string, List<string>> RequirementSources { get; set; } = new(StringComparer.Ordinal);
    public List<string> GlobalSourceIds { get; set; } = [];
    public PlanningConstructionStep? CurrentStep { get; set; }
    public Dictionary<string, List<PlanningCommitment>> PendingCommitments { get; set; } = new(StringComparer.Ordinal);
    public int NextStep { get; set; }
    public int IndexedAnswers { get; set; }
    public int RequirementOffset { get; set; }
    public bool RequirementPageRequested { get; set; }
    public int InterfaceOffset { get; set; }
    public int OutputOffset { get; set; }
    public List<string> FailedActions { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlanningConstructionProgress? Progress { get; set; }
    public List<JsonObject> RejectedSteps { get; set; } = [];
    public string? ReplacementScope { get; set; }
    public List<string>? ReplacementRequirements { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningConstructionSuspendedStep>? SuspendedSteps { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ResumingStepId { get; set; }
}

/// <summary>Observed facts and call accounting for the existing construction loop; never planning authority.</summary>
public sealed class PlanningConstructionProgress
{
    public List<string> Evidence { get; set; } = [];
    public List<string> Behaviors { get; set; } = [];
    public List<string> CompletedRequirements { get; set; } = [];
    public int CountedModelCalls { get; set; }
    public int NonProductiveModelCalls { get; set; }
}

/// <summary>An explicit replacement decision, not an interpretation of a diagnostic's prose.</summary>
public sealed class PlanningConstructionReselection
{
    public string Reason { get; set; } = "";
    public string Disposition { get; set; } = "";
    public PlanningConstructionTypedPrerequisite? Prerequisite { get; set; }
}

/// <summary>A needed value and its original evidence; no future executable producer identity.</summary>
public sealed class PlanningConstructionTypedPrerequisite
{
    public string Need { get; set; } = "";
    public TaskType ExpectedType { get; set; } = new();
    public List<string> EvidenceAddresses { get; set; } = [];
}

/// <summary>A retained consumer in the bounded prerequisite chain, resumed through an exact selected or reviewed binding.</summary>
public sealed class PlanningConstructionSuspendedStep
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public PlanningConstructionStep Consumer { get; set; } = new();
    public PlanningConstructionTypedPrerequisite Prerequisite { get; set; } = new();
    public string Status { get; set; } = "waiting";
    public int RejectedStepIndex { get; set; }
    public int SuspendedAtCheckpoint { get; set; }
    public int LastPrerequisiteCheckpoint { get; set; }
    public Dictionary<string, string> SourceVersions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EvidenceHashes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ContractHashes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> DependencyHashes { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupHash { get; set; }
}

/// <summary>An exact retained source page or a verbatim indexed clause.</summary>
public sealed class PlanningConstructionSource
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Text { get; set; } = "";
    public string Kind { get; set; } = "page";
    public bool Indexed { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinuationOf { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TaskInput>? Inputs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TaskInput>? Outputs { get; set; }
}

/// <summary>One immediate business step and its host-checked selection, not a future workflow outline.</summary>
public sealed class PlanningConstructionStep
{
    /// <summary>Owning business responsibility, independent of this implementation cursor.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BlockId { get; set; }
    public string Id { get; set; } = "";
    public string BaseHash { get; set; } = "";
    public string Scope { get; set; } = "/root";
    public string? Kind { get; set; } = "value";
    public string Objective { get; set; } = "";
    public List<string> RequirementIds { get; set; } = [];
    public List<string> InputTaskIds { get; set; } = [];
    public List<TaskValue> Inputs { get; set; } = [];
    public List<string> DependencyTaskIds { get; set; } = [];
    public List<string> ExpectedOutputs { get; set; } = [];
    public List<string> OperationIds { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; set; }
    public List<string> SchemaPointers { get; set; } = [];
    public List<string> CloseRequirementIds { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CloseScopes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlanningConstructionContinuationBinding? ContinuationBinding { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningConstructionBoundaryTransition>? BoundaryTransitions { get; set; }
    public List<string> OutputNames { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningConstructionPrerequisite>? Prerequisites { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlanningConstructionDiscoveryContinuation? PendingDiscovery { get; set; }
    public bool DiscoveryComplete { get; set; }
}

/// <summary>Retained business selection and its reviewed completion, never executable work or scheduling authority.</summary>
public sealed class PlanningConstructionBlock
{
    public string Id { get; set; } = "";
    public string Objective { get; set; } = "";
    public List<string> RequirementIds { get; set; } = [];
    public List<string> InformationNeeds { get; set; } = [];
    public List<string> ExpectedResults { get; set; } = [];
    public List<PlanningConstructionBlockDependency> Dependencies { get; set; } = [];
    public List<string> SuccessCriteria { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletionCheckpoint { get; set; }
}

public sealed class PlanningConstructionBlockDependency
{
    public string ResultId { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>A proposed semantic association reviewed with a producer fragment; not an executable reference or scheduling edge.</summary>
public sealed class PlanningConstructionContinuationBinding
{
    public string ContinuationId { get; set; } = "";
    public TaskValue Binding { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DischargedContinuationIds { get; set; }
}

/// <summary>One exact existing-boundary claim reviewed with its producer; neither future tasks nor requirement completion.</summary>
public sealed class PlanningConstructionBoundaryTransition
{
    public string Scope { get; set; } = "";
    public List<string> RequirementIds { get; set; } = [];
    public List<TaskOutput> Outputs { get; set; } = [];
    public bool CloseScope { get; set; }
}

/// <summary>Continuation of one received metadata action; never another model request or executable task.</summary>
public sealed class PlanningConstructionDiscoveryContinuation
{
    public string ActionHash { get; set; } = "";
    public List<PlanningDiscoveryRequest> Requests { get; set; } = [];
    public List<PlanningDiscoveryRequest> Pending { get; set; } = [];
    public List<string> Visited { get; set; } = [];
    public int RemainingReads { get; set; } = 4;
}

/// <summary>Original evidence for a current business prerequisite, never a future task or permission.</summary>
public sealed class PlanningConstructionPrerequisite
{
    public string Need { get; set; } = "";
    public List<string> EvidenceAddresses { get; set; } = [];
}

/// <summary>An explicit candidate-only binding of original intent to an inspected producer.</summary>
public sealed class PlanningConstructionCommitment
{
    public string RequirementId { get; set; } = "";
    public string Operation { get; set; } = "";
    public PlanningCommittedResult? Result { get; set; }
}
