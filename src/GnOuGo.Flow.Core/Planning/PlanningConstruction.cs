using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Captured construction limits. These govern model requests, never executable permissions.</summary>
public sealed class PlanningConstructionOptions
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningConstructionPhasePolicy? PhasePolicy { get; set; }
    public int MaxTasksPerDraft { get; set; } = 8;
    public int MaxCheckpoints { get; set; } = 128;
    public int MaxCorrectionsPerDraft { get; set; } = 3;
    public int DraftOutputTokens { get; set; } = 8192;
    public int ReviewOutputTokens { get; set; } = 2048;
}

/// <summary>Non-executable construction journal. Plan remains the accepted semantic source of truth.</summary>
public sealed class PlanningConstructionState
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningCompactConstructionState? Compact { get; set; }
    public PlanningConstructionOptions Limits { get; set; } = new();
    public List<string> OpenScopes { get; set; } = ["/root"];
    public List<PlanningConstructionCheckpoint> Checkpoints { get; set; } = [];
    public PlanningConstructionCandidate? Candidate { get; set; }
    public List<PlanningConstructionRequirement> Requirements { get; set; } = [];
    public List<string> FailedCandidates { get; set; } = [];
    public List<string> EvidenceRequests { get; set; } = [];
    public JsonObject? Evidence { get; set; }
    public JsonObject? PendingReviewContext { get; set; }
    public string? PendingReviewHash { get; set; }
    public int RequirementCursor { get; set; }
    public int TaskCursor { get; set; }
    public int ScopeCursor { get; set; }
    public bool Complete { get; set; }
    public string? IssuedBaseHash { get; set; }
    public List<PlanTask> RebuildTasks { get; set; } = [];
    public List<PlanningConstructionRebuildScope> RebuildScopes { get; set; } = [];
    public List<PlanningConstructionInvalidation> Invalidations { get; set; } = [];
    public int DraftCorrections { get; set; }
    public string? LastProposalHash { get; set; }
}

public sealed class PlanningConstructionRebuildScope
{
    public string Path { get; set; } = "/root";
    public List<string> Tasks { get; set; } = [];
    public List<string> Always { get; set; } = [];
    public List<TaskOutput> Outputs { get; set; } = [];
}

public sealed class PlanningConstructionInvalidation
{
    public int Sequence { get; set; }
    public string BeforeHash { get; set; } = "";
    public string AfterHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<PlanTask> Tasks { get; set; } = [];
    public List<PlanningConstructionRebuildScope> Scopes { get; set; } = [];
}

public sealed class PlanningConstructionCandidate
{
    public string BaseHash { get; set; } = "";
    public string Hash { get; set; } = "";
    public TaskPlan Plan { get; set; } = new();
    public PlanningConstructionExtension Extension { get; set; } = new();
    public List<string> OpenScopes { get; set; } = [];
    public List<string> TaskIds { get; set; } = [];
    public List<string> ImportedTaskIds { get; set; } = [];
    public List<PlanningDiagnostic> Diagnostics { get; set; } = [];
    public List<JsonObject> Reviews { get; set; } = [];
    public int Corrections { get; set; }
    public bool BusinessReviewed { get; set; }
    public List<string>? AuthorizedPaths { get; set; }
}

/// <summary>A bounded edit envelope containing only existing semantic TaskPlan types.</summary>
public sealed class PlanningConstructionExtension
{
    public string Scope { get; set; } = "/root";
    public List<PlanTask> Tasks { get; set; } = [];
    public List<PlanTask> Always { get; set; } = [];
    public List<TaskOutput> Outputs { get; set; } = [];
    public List<TaskGroup> Groups { get; set; } = [];
    public List<PlanningChoice> Choices { get; set; } = [];
    public List<string> CloseScopes { get; set; } = [];
    public List<string> RequirementIds { get; set; } = [];
    public bool Complete { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningConstructionContinuationBinding? ContinuationBinding { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningConstructionBoundaryTransition>? BoundaryTransitions { get; set; }
}

public sealed class PlanningConstructionCheckpoint
{
    public int Sequence { get; set; }
    public string BaseHash { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public List<string> TaskIds { get; set; } = [];
    public List<string> ImportedTaskIds { get; set; } = [];
    public List<string> RequirementIds { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ClosedRequirementIds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanningConstructionStep? Step { get; set; }
    public List<string> ClosedScopes { get; set; } = [];
    public JsonObject Fragment { get; set; } = new();
    public List<JsonObject> Reviews { get; set; } = [];
    public List<string> InvalidatedTaskIds { get; set; } = [];
}

public sealed class PlanningConstructionRequirement
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "remaining";
    public List<int> Checkpoints { get; set; } = [];
}
