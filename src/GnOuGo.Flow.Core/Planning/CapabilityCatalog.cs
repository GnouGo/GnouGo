using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Discovery is not authorization. Only resolved and validated contracts enter the executable catalog.</summary>
public interface ICapabilityCatalog
{
    Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct);
    Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null);
    Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct);
}

public sealed record CapabilitySource(string Id, string Description);
public sealed record CapabilitySummary(string Id, string SourceId, string Name, string Description,
    string StepType, string EffectKind, string Version, McpCapabilityComposition? Composition = null, PlanningOperation? Operation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] McpArtifactContract? ArtifactContract = null);
public sealed record CapabilityPage(string SourceId, string? Cursor, List<CapabilitySummary> Capabilities,
    string? NextCursor, string? UnavailableReason = null, string? Query = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ProducedArtifactKind = null);

public sealed class CapabilityDiscoveryState
{
    /// <summary>Retained batch query for historical receipts. Ranking derives source focus from pages and accepted intent.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PresentationQuery { get; set; }
    public List<CapabilitySource> Sources { get; set; } = [];
    public List<CapabilityPage> Pages { get; set; } = [];
    public List<PlanningCapability> Resolved { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    /// <summary>Per-source contract inspection selections, not execution authorization.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningDiscoveryRequest>? Inspections { get; set; }
}

/// <summary>Reviewable goals, not a second executable program.</summary>
public sealed class PlanningRequirements
{
    public string Summary { get; set; } = "";
    public List<PlanningRequirement> Outcomes { get; set; } = [];
    /// <summary>Accepted caller interface. Null is unresolved or a historical declaration; empty means no caller inputs.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TaskInput>? Inputs { get; set; }
    /// <summary>Accepted business result interface. Null preserves historical declarations; defaults are not output evidence.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TaskInput>? Outputs { get; set; }
}

public sealed record PlanningRequirement(string Id, string Description)
{
    /// <summary>Version 3 review evidence: an inspected operation determines the external effect.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }
    /// <summary>Data production or an authoritative operation effect. Null retains historical intent.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Execution { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Always { get; init; }
    /// <summary>Whether execution may depend on a branch or a possibly empty collection.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Conditional { get; init; }
    /// <summary>Null/once requires an invocation; each_item requires coverage of every iteration of the bound foreach.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Coverage { get; init; }
}

/// <summary>Review annotations referencing the TaskPlan, never another executable representation.</summary>
public sealed record PlanningOutcomeBinding(string OutcomeId, List<string> TaskIds, List<string> Outputs)
{
    /// <summary>Accepted public inputs supporting a data-only outcome; never external execution evidence.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Inputs { get; init; }
    /// <summary>Existing TaskPlan foreach identity for an accepted each_item outcome; never executable structure.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ForEachTaskId { get; init; }
}

/// <summary>A bounded discovery request or complete semantic task proposal; the host validates exclusivity.</summary>
public sealed class PlanningProposal
{
    /// <summary>Issued once; omitted after the host accepts the requirements.</summary>
    public PlanningRequirements? Requirements { get; set; }
    public List<PlanningDiscoveryRequest>? DiscoveryRequests { get; set; }
    public TaskPlan? Plan { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningOutcomeBinding>? OutcomeBindings { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PlanningQuestion>? Clarifications { get; set; }
    public string Explanation { get; set; } = "";
}

/// <summary>Intent clarification, never executable values or permission grants.</summary>
public sealed record PlanningQuestion(string Id, string Question, List<PlanningQuestionAlternative> Alternatives, string? Recommended);
public sealed record PlanningQuestionAlternative(string Id, string Description);
public sealed record PlanningAnswer(string QuestionId, string? AlternativeId = null, string? Text = null);
public sealed record PlanningAnswerBatch(long Revision, List<PlanningQuestion> Questions, List<PlanningAnswer> Answers);

public sealed record PlanningDiscoveryRequest(string SourceId, string? Cursor = null, string? Query = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<string>? OperationIds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ProducedArtifactKind = null);

public static class PlanningPhase
{
    public const string Requirements = "requirements", Discovery = "discovery", Tasks = "tasks",
        Validation = "validation", Review = "review", Replanning = "replanning";
}
