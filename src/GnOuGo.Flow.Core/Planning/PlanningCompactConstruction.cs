using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Version-two presentation and scheduling metadata; never an executable plan.</summary>
public sealed class PlanningCompactConstructionState
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? PresentationVersion { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningConstructionInterfaceExtraction>? InterfaceExtractions { get; set; }
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
    public List<JsonObject> RejectedSteps { get; set; } = [];
    public string? ReplacementScope { get; set; }
    public List<string>? ReplacementRequirements { get; set; }
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
    public string? InterfaceKind { get; set; }
}

/// <summary>A bounded original declaration awaiting typed interface extraction, never executable work.</summary>
public sealed class PlanningConstructionInterfaceExtraction
{
    public string Id { get; set; } = "";
    public List<string> SourceIds { get; set; } = [];
    public bool Inputs { get; set; }
    public bool Outputs { get; set; }
    public bool Complete { get; set; }
}

/// <summary>One immediate business step and its host-checked selection, not a future workflow outline.</summary>
public sealed class PlanningConstructionStep
{
    public string Id { get; set; } = "";
    public string BaseHash { get; set; } = "";
    public string Scope { get; set; } = "/root";
    public string Kind { get; set; } = "value";
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
    public List<string> OutputNames { get; set; } = [];
    public bool DiscoveryComplete { get; set; }
}

/// <summary>An explicit candidate-only binding of original intent to an inspected producer.</summary>
public sealed class PlanningConstructionCommitment
{
    public string RequirementId { get; set; } = "";
    public string Operation { get; set; } = "";
    public PlanningCommittedResult? Result { get; set; }
}
