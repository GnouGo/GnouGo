using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Discovery is not authorization. Only resolved and validated contracts enter the executable catalog.</summary>
public interface ICapabilityCatalog
{
    Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct);
    Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct);
    Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct);
}

public sealed record CapabilitySource(string Id, string Description);
public sealed record CapabilitySummary(string Id, string SourceId, string Name, string Description,
    string StepType, string EffectKind, string Version, McpCapabilityComposition? Composition = null);
public sealed record CapabilityPage(string SourceId, string? Cursor, List<CapabilitySummary> Capabilities,
    string? NextCursor, string? UnavailableReason = null);

public sealed class CapabilityDiscoveryState
{
    public List<CapabilitySource> Sources { get; set; } = [];
    public List<CapabilityPage> Pages { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}

/// <summary>Reviewable goals, not a second executable program.</summary>
public sealed class PlanningRequirements
{
    public string Summary { get; set; } = "";
    public List<PlanningRequirement> Outcomes { get; set; } = [];
    public List<PlanningQuestion> Questions { get; set; } = [];
}

public sealed record PlanningRequirement(string Id, string Description, List<string> StageIds);
public sealed record PlanningQuestion(string Id, string Question, PlanningSchema AnswerType);

/// <summary>A bounded discovery request or complete graph proposal; the host validates exclusivity.</summary>
public sealed class PlanningProposal
{
    public PlanningRequirements Requirements { get; set; } = new();
    public string? SourceId { get; set; }
    public string? Cursor { get; set; }
    public List<string> CapabilityIds { get; set; } = [];
    public PlanningGraph? Graph { get; set; }
    public string Explanation { get; set; } = "";
}

public static class PlanningPhase
{
    public const string Requirements = "requirements", Discovery = "discovery", Graph = "graph",
        Validation = "validation", Review = "review", Replanning = "replanning";
}
