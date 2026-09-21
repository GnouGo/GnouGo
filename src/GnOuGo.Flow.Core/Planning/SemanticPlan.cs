namespace GnOuGo.Flow.Core.Planning;

/// <summary>Business requirements. Types describe desired values, never tool result contracts.</summary>
public sealed class SemanticPlan
{
    public string Summary { get; set; } = "";
    public List<SemanticPort> Inputs { get; set; } = [];
    public List<SemanticAction> Actions { get; set; } = [];
    public List<SemanticBinding> Outputs { get; set; } = [];
    public List<SemanticSubflow> Subflows { get; set; } = [];
    public List<PlanningQuestion> Questions { get; set; } = [];
}

public sealed record SemanticPort(string Name, string Description, BusinessType? Type = null, bool Optional = false);
/// <summary>A named business input and its source, such as input.request or action.result.</summary>
public sealed record SemanticBinding(string Name, string Source);
public sealed record SemanticBlock(string Name, List<SemanticAction> Actions, List<SemanticBinding> Outputs);
public sealed record SemanticSubflow(string Name, List<SemanticPort> Inputs, List<SemanticAction> Actions, List<SemanticBinding> Outputs);

public sealed class SemanticAction
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "action";
    public string Purpose { get; set; } = "";
    public List<SemanticBinding> Inputs { get; set; } = [];
    public List<SemanticPort> Outputs { get; set; } = [];
    public List<string> After { get; set; } = [];
    public string? Condition { get; set; }
    public List<SemanticBlock> Blocks { get; set; } = [];
}

/// <summary>Host-owned coverage of an immutable authorized catalog and semantic plan.</summary>
public sealed class CapabilityGrounding
{
    public string CatalogHash { get; set; } = "";
    public string SemanticHash { get; set; } = "";
    public List<GroundingPage> Pages { get; set; } = [];
    public List<GroundingPageResult> Results { get; set; } = [];
    public List<GroundingSelection>? Selections { get; set; }
}

public sealed record GroundingPage(string Id, List<string> ActionIds, List<string> CapabilityIds);
public sealed record GroundingPageResult(string PageId, List<GroundingDecision> Decisions);
public sealed record GroundingDecision(string ActionId, string Outcome, List<GroundingMatch> Matches, string Reason);
public sealed record GroundingSelection(string ActionId, List<string> CapabilityIds, string Reason);
public sealed record GroundingMatch(string CapabilityId, string Reason);

public static class PlanningPhase
{
    public const string Semantic = "semantic", Grounding = "grounding", Binding = "binding",
        Validation = "validation", Scenarios = "scenarios", Review = "review", Replanning = "replanning";
}
