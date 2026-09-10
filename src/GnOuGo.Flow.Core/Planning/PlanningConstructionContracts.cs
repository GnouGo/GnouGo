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
    public bool Resolved { get; set; }
}

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
