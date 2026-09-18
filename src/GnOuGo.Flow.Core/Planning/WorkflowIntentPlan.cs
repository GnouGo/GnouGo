using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
namespace GnOuGo.Flow.Core.Planning;

/// <summary>Untrusted interpretation. It contains no catalog, security policy, or approval.</summary>
public sealed class WorkflowIntentPlan
{
    public string Summary { get; set; } = "";
    public string Entrypoint { get; set; } = "main";
    public string? Functions { get; set; }
    public List<WorkflowIntent> Workflows { get; set; } = [];
    public PlanningFixtures? Fixtures { get; set; }
    public List<PlanningQuestion> Questions { get; set; } = [];
}
public sealed class WorkflowIntent
{
    public string Key { get; set; } = "main";
    public string Purpose { get; set; } = "";
    public List<PlanningPort> Inputs { get; set; } = [];
    public List<PlanningOutput> Outputs { get; set; } = [];
    public List<WorkflowIntentStep> Steps { get; set; } = [];
    public List<WorkflowIntentStep> Finally { get; set; } = [];
    public string? Functions { get; set; }
}
public sealed class WorkflowIntentStep
{
    public string Key { get; set; } = "";
    /// <summary>invoke, or an allowed native step type. invoke requires a catalog capability.</summary>
    public string Kind { get; set; } = "invoke";
    public string Purpose { get; set; } = "";
    public string? CapabilityId { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public PlanningValue Input { get; set; } = new() { Kind = "object" };
    public PlanningValue? If { get; set; }
    public PlanningValue? Expr { get; set; }
    public PlanningSchema? OutputSchema { get; set; }
    public PlanningStructuredOutput? StructuredOutput { get; set; }
    public string? Output { get; set; }
    public string? ItemVar { get; set; }
    public string? IndexVar { get; set; }
    public RetryPolicy? Retry { get; set; }
    public List<PlanningErrorCase> OnError { get; set; } = [];
    public List<WorkflowIntentStep> Steps { get; set; } = [];
    public List<WorkflowIntentBranch> Branches { get; set; } = [];
    public List<WorkflowIntentCase> Cases { get; set; } = [];
    public List<WorkflowIntentStep> Default { get; set; } = [];
}
public sealed record WorkflowIntentBranch(List<WorkflowIntentStep> Steps);
public sealed record WorkflowIntentCase(string? Value, PlanningValue? When, List<WorkflowIntentStep> Steps);
public sealed class PlanningFixtures
{
    public PlanningValue? Inputs { get; set; }
    public List<PlanningObservation> Observations { get; set; } = [];
}
public sealed record PlanningQuestion(string Id, string Question, PlanningSchema AnswerSchema);
public sealed record PlanningObservation(string Workflow, string Node, List<PlanningValue> Responses);
/// <summary>A missing graph field. Choice domains are recomputed, never persisted.</summary>
public sealed record PlanningHole(string Id, string WorkflowKey, string? NodeKey, string Path, string Kind, JsonObject? ExpectedSchema, bool Optional = false);
