using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>A coordinator-owned executable field, never a model-selected graph address.</summary>
public sealed class PlanningHole
{
    public string Id { get; set; } = "";
    public string WorkflowKey { get; set; } = "";
    public string? NodeKey { get; set; }
    public string Path { get; set; } = "";
    public string CanonicalLocation { get; set; } = "";
    public string Kind { get; set; } = "value";
    public string Purpose { get; set; } = "";
    public JsonObject? ExpectedSchema { get; set; }
    /// <summary>The declared argument may be omitted, subject to remaining dependency obligations.</summary>
    public bool Optional { get; set; }
    public bool Resolved { get; set; }
    public string? ResolutionOrigin { get; set; }
    public bool Superseded { get; set; }
    public List<string> ExposedRequests { get; set; } = [];
    public int? DirectCandidateCount { get; set; }
    public int? ComputationParameterCount { get; set; }
    public string? ModelRequiredReason { get; set; }
}

/// <summary>Receipt-backed accounting. Null usage means unavailable, never zero usage.</summary>
public sealed class PlanningRequestAccounting
{
    public string Id { get; set; } = "";
    public long Revision { get; set; }
    public string WorkflowKey { get; set; } = "$plan";
    public string Phase { get; set; } = "";
    public string Gate { get; set; } = "";
    public string Purpose { get; set; } = "assessment";
    public int EstimatedInputTokens { get; set; }
    public string Evidence { get; set; } = "reserved";
    public bool? Repair { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public Dictionary<string, string> HoleReasons { get; set; } = new(StringComparer.Ordinal);
    public int? AvoidableDispatches { get; set; }
    public int? AvoidableExtraRequests { get; set; }
}

public sealed record PlanningRequestCounts(string WorkflowKey, string Phase, string Gate, int Reservations, int ModelUsed,
    int Unverifiable, int EstimatedInputTokens, long? InputTokens, long? OutputTokens, int? AvoidableDispatches, int? AvoidableExtraRequests,
    int? Repairs = null, int? Failures = null);

/// <summary>Encrypted assignment delta against an exact graph; not a second authoritative graph.</summary>
public sealed class PlanningStagedAssignments
{
    public string WorkflowKey { get; set; } = "";
    public string GraphFingerprint { get; set; } = "";
    public string WorkflowFingerprint { get; set; } = "";
    public string DependencyFingerprint { get; set; } = "";
    public string ScopeFingerprint { get; set; } = "";
    public List<PlanningHole> Targets { get; set; } = [];
    public JsonObject Payload { get; set; } = new();
    public JsonObject ResponseSchema { get; set; } = new();
    public Dictionary<string, PlanningValue> Bindings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ParameterScopes { get; set; } = new(StringComparer.Ordinal);
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<string> RejectedCandidates { get; set; } = [];
    public int Stage { get; set; }
}

public sealed class PlanningGateAllowance
{
    public string WorkflowKey { get; set; } = "";
    public string Gate { get; set; } = "";
    public int Attempts { get; set; }
}

public sealed class PlanningGateProgress
{
    public string WorkflowKey { get; set; } = "";
    public string Gate { get; set; } = "";
    public int Failures { get; set; }
    public List<string> Evaluations { get; set; } = [];
    public Dictionary<string, string> EvaluationPhases { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PlanningGateCounts(string Gate, int Repairs, int Failures);

public sealed record PlanningHoleProgress(string Id, int? DirectBindings, int? ComputationParameters);

/// <summary>Human-requested behavior revision, located before any candidate field is edited.</summary>
public sealed class PlanningBehaviorRevisionState
{
    public string? ReviewedBaselineBehavior { get; set; }
    public string Text { get; set; } = "";
    public bool Located { get; set; }
    public List<PlanningBehaviorRevisionField> Fields { get; set; } = [];
}

public sealed record PlanningBehaviorRevisionField(string Path, string CanonicalLocation, string Operation, string OriginalFingerprint, string Evidence);

public static class PlanningGates
{
    public const string Response = "response_contract";
    public const string Behavior = "behavior_contract";
    public const string Typed = "typed_dataflow";
    public const string Compilation = "compilation";
    public const string Scenarios = "scenarios";
    public const string Semantic = "semantic_review";
    public static string FromStage(int stage) => stage switch
    {
        0 => Response, 1 => Typed, 2 => Compilation, 3 => Scenarios, _ => Semantic
    };
}
