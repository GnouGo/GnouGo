using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Planning;

public static class PlanningMode
{
    public const string Auto = "auto", Interactive = "interactive";
    public static void Validate(string mode)
    {
        if (mode is not (Auto or Interactive)) throw new ArgumentException("Unknown planning mode.");
    }
}

/// <summary>A business decision, independent of workflow-runtime human input.</summary>
public sealed class PlanningDecision
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Context { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Scope { get; set; } = "";
    public List<string> ActionIds { get; set; } = [];
    public List<PlanningDecisionOption> Options { get; set; } = [];
    public bool AllowCustomAnswer { get; set; }
}

public sealed record PlanningDecisionOption(string Id, string Label, string Reason, bool Preferred);
public sealed record PlanningDecisionAnswer(string DecisionId, string? OptionId = null, string? Text = null);
public sealed record PlanningDecisionRecord(PlanningDecision Decision, PlanningDecisionAnswer Answer, string Source, string Reason, DateTimeOffset AnsweredAtUtc);

/// <summary>Host-owned continuation; candidate payloads are never client-authored commands.</summary>
public sealed class PlanningDecisionContinuation
{
    public string Operation { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string Prompt { get; set; } = "";
    public JsonObject Schema { get; set; } = new();
    public JsonObject Candidates { get; set; } = new();
    public PlanningDecisionAnswer? Answer { get; set; }
}

/// <summary>The workflow owner collects planner commands separately from runtime human.input.</summary>
public interface IPlanningDecisionProvider
{
    Task<PlanningCommand> RequestAsync(PlanningSession session, CancellationToken ct);
    Task CheckpointedAsync(PlanningSession session, CancellationToken ct);
}
