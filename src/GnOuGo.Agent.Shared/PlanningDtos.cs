using System.Text.Json.Nodes;
namespace GnOuGo.Agent.Shared;
public sealed record PlanningStartDto(string Name, string Prompt, bool ReviseExisting = false);
public sealed record PlanningGenerationDto(string Reasoning = "medium", int MaxInputTokensPerRequest = 12_000, int MaxOutputTokens = 8_192);
public sealed record PlanningCommandDto(string Kind, long ExpectedRevision, string? ArtifactHash = null, string? Text = null, JsonObject? Answers = null, PlanningGenerationDto? Generation = null);
public sealed record PlanningValidationDto(string Code, string Location, string Message, bool Required);
public sealed record PlanningScenarioDto(string Id, string Outcome, string Description);
public sealed record PlanningQuestionDto(string Id, string Question, JsonObject AnswerSchema);
public sealed record PlanningSessionDto(string Id, string Name, long Revision, string Status, string Summary, string Diagram,
    JsonObject? SemanticPlan, JsonObject? GroundedPlan, string? Yaml, string? ArtifactHash, string? ApprovedHash,
    IReadOnlyList<PlanningValidationDto> Diagnostics, IReadOnlyList<PlanningScenarioDto> Scenarios,
    IReadOnlyList<PlanningQuestionDto> Questions, int Calls, int ReplanAttempts, long InputTokens, long OutputTokens,
    decimal EstimatedCost, string Currency, double ActiveMilliseconds, double HumanWaitMilliseconds, string Phase);
