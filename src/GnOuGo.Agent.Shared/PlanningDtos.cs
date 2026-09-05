using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Shared;

public sealed record PlanningStartDto(string Name, string Prompt, bool ReviseExisting = false);
public sealed record PlanningCommandDto(string Kind, long ExpectedRevision, string? ArtifactHash = null, string? Text = null, JsonObject? Answers = null);
public sealed record PlanningValidationDto(string Code, string Location, string Message, bool Required);
public sealed record PlanningScenarioDto(string Id, string Outcome, string Description);
public sealed record PlanningRevisionDto(long Revision, string ArtifactHash, string Status, IReadOnlyList<string> ChangedFragments);
public sealed record PlanningSessionDto(
    string Id, string Name, long Revision, string Status, string Summary, string Diagram,
    IReadOnlyList<string> BehaviorDetails, string? Yaml, string? ArtifactHash, string? ApprovedHash,
    double ActiveMilliseconds, double HumanWaitMilliseconds,
    IReadOnlyList<PlanningValidationDto> Diagnostics, IReadOnlyList<PlanningScenarioDto> Scenarios,
    IReadOnlyList<PlanningRevisionDto> History, JsonObject? Question,
    long Calls, long InputTokens, long OutputTokens, decimal EstimatedCost, string Currency, string? Outcome = null);
