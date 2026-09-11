using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Shared;

public sealed record PlanningStartDto(string Name, string Prompt, bool ReviseExisting = false);
public sealed record PlanningGenerationDto(string? Reasoning = null, int MaxInputTokensPerRequest = 12_000, int MaxOutputTokens = 8_192);
public sealed record PlanningCommandDto(string Kind, long ExpectedRevision, string? ArtifactHash = null, string? Text = null, JsonObject? Answers = null, PlanningGenerationDto? Generation = null);
public sealed record PlanningWorkflowDto(string Key, string Status, IReadOnlyList<string> Dependencies, int Calls, int RepairCalls,
    int? EstimatedInputTokens, int? InputTokenLimit, int UnresolvedFields = 0, int ResolvedFields = 0, string? Gate = null, int RepairsConsumed = 0, int RepairsAllowed = 5,
    int TotalHoles = 0, int DeterministicallyResolvedHoles = 0, int ModelHoles = 0, int ModelHoleExposures = 0,
    IReadOnlyList<PlanningHoleChoiceDto>? HoleChoices = null, IReadOnlyList<PlanningGateProgressDto>? Gates = null,
    int DeterministicSchemaHoles = 0, int ModelSchemaHoles = 0, int? ModelRequired = null, int? ModelUsed = null);
public sealed record PlanningRequestCountsDto(string WorkflowKey, string Phase, string Gate, int Reservations, int ModelUsed, int Unverifiable,
    int EstimatedInputTokens, long? InputTokens, long? OutputTokens, int? AvoidableDispatches, int? AvoidableExtraRequests, int? Repairs = null, int? Failures = null);
public sealed record PlanningHoleChoiceDto(string Id, int? DirectBindings, int? ComputationParameters);
public sealed record PlanningGateProgressDto(string Gate, int Repairs, int Failures, string? WorkflowKey = null);
public sealed record PlanningValidationDto(string Code, string Location, string Message, bool Required);
public sealed record PlanningScenarioDto(string Id, string Outcome, string Description);
public sealed record PlanningRevisionDto(long Revision, string ArtifactHash, string Status, IReadOnlyList<string> ChangedWorkflows);
public sealed record PlanningSessionDto(
    string Id, string Name, long Revision, string Status, string Summary, string Diagram,
    IReadOnlyList<string> BehaviorDetails, string? Yaml, string? ArtifactHash, string? ApprovedHash,
    double ActiveMilliseconds, double HumanWaitMilliseconds,
    IReadOnlyList<PlanningValidationDto> Diagnostics, IReadOnlyList<PlanningScenarioDto> Scenarios,
    IReadOnlyList<PlanningRevisionDto> History, JsonObject? Question,
    long Calls, long InputTokens, long OutputTokens, decimal EstimatedCost, string Currency, string? Outcome = null,
    string? CurrentPhase = null, JsonObject? BehaviorPlan = null,
    string? ApprovedBehaviorHash = null, string? RecoverySummary = null, int AnsweredForms = 0,
    string? Model = null, string? Reasoning = null, IReadOnlyList<PlanningWorkflowDto>? Workflows = null, string? DataflowFingerprint = null, int BindingCount = 0, string? PreparationStage = null, int DecisionContractVersion = 0, int DecisionCount = 0, IReadOnlyList<PlanningGateProgressDto>? Gates = null,
    IReadOnlyList<PlanningRequestCountsDto>? RequestCounts = null);
