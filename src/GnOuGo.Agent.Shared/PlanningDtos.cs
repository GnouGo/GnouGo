using System.Text.Json.Nodes;
namespace GnOuGo.Agent.Shared;
public sealed record PlanningStartDto(string Name, string Prompt, bool ReviseExisting = false, string Mode = "interactive",
    string? ConstructionPreset = null, string ThresholdMode = "strict");
public sealed record PlanningGenerationDto(string Reasoning = "medium", int MaxInputTokensPerRequest = 32_000, int MaxOutputTokens = 32_768);
public sealed record PlanningCommandDto(string Kind, long ExpectedRevision, string? ArtifactHash = null, string? Text = null, JsonObject? Selections = null, PlanningGenerationDto? Generation = null, string? Mode = null,
    IReadOnlyList<PlanningAnswerDto>? Answers = null, string? RequestId = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ReviewedRequirementIds { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PreserveRequirements { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EditablePaths { get; init; }
}
public sealed record PlanningQuestionDto(string Id, string Question, IReadOnlyList<PlanningAlternativeDto> Alternatives, string? Recommended);
public sealed record PlanningAnswerDto(string QuestionId, string? AlternativeId = null, string? Text = null);
public sealed record PlanningValidationDto(string Code, string Location, string Message, bool Required)
{
    public string? ValidationStage { get; init; }
    public string? Rule { get; init; }
    public PlanningPrerequisiteContextDto? Prerequisite { get; init; }
}
public sealed record PlanningPrerequisiteContextDto(string Kind, string Description, string? Output, string? ConsumerCapability, string? ContractPath, string? RootActionId);
public sealed record PlanningValidationResultDto(string Id, string Outcome, string Description);
public sealed record PlanningAlternativeDto(string Id, string Description, JsonNode? Value);
public sealed record PlanningChoiceDto(string Id, string Question, IReadOnlyList<PlanningAlternativeDto> Alternatives, string Recommended, string? Selected);
public sealed record PlanningSessionDto(string Id, string Name, long Revision, string Status, string Summary, string Diagram,
    JsonObject? Requirements, JsonObject? Graph, string? Yaml, string? ArtifactHash, string? ApprovedHash,
    IReadOnlyList<PlanningValidationDto> Diagnostics, IReadOnlyList<PlanningValidationResultDto> ValidationResults,
    IReadOnlyList<PlanningChoiceDto> Choices, int Calls, int ReplanAttempts, long InputTokens, long OutputTokens,
    decimal EstimatedCost, string Currency, double ActiveMilliseconds, double HumanWaitMilliseconds, string Phase, string Mode = "interactive", bool WorkflowSession = false)
{
    public int SchemaVersion { get; init; } = 11;
    public string ThresholdMode { get; init; } = "strict";
    public JsonObject? EffectivePolicy { get; init; }
    public JsonObject? OverallProgress { get; init; }
    public IReadOnlyList<PlanningInspectionEventDto> Events { get; init; } = [];
    public IReadOnlyList<PlanningThresholdWarningDto> ThresholdWarnings { get; init; } = [];
    public IReadOnlyList<string> RevisionScope { get; init; } = [];
    public IReadOnlyList<string> DiscoveryLimitations { get; init; } = [];
    public JsonObject? TaskPlan { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestText { get; init; }
    public IReadOnlyList<PlanningQuestionDto> Questions { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlanningRequestInspectionDto? RequestInspection { get; init; }
}
public sealed record PlanningRequestInspectionDto(string State, string? RequestId, bool CancellationRequested = false);
public sealed record PlanningInspectionEventDto(long Sequence, DateTimeOffset TimestampUtc, string Phase, string State, string Reason,
    string? RequestId, string? BlockId, string? StepId, int? Checkpoint, string? Objective)
{
    public long PhaseOccurrence { get; init; }
}
public sealed record PlanningThresholdWarningDto(string Kind, long Current, long Threshold, string Origin, string Stage,
    string? RequestId, string? BlockId, string Consequence = "Continues; mandatory controls remain enforced.");
