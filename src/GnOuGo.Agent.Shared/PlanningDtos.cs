using System.Text.Json.Nodes;
namespace GnOuGo.Agent.Shared;
public sealed record PlanningStartDto(string Name, string Prompt, bool ReviseExisting = false, string Mode = "interactive");
public sealed record PlanningGenerationDto(string Reasoning = "medium", int MaxInputTokensPerRequest = 12_000, int MaxOutputTokens = 8_192);
public sealed record PlanningCommandDto(string Kind, long ExpectedRevision, string? ArtifactHash = null, string? Text = null, JsonObject? Selections = null, PlanningGenerationDto? Generation = null, string? Mode = null);
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
    public int SchemaVersion { get; init; } = 10;
    public IReadOnlyList<string> RevisionScope { get; init; } = [];
    public IReadOnlyList<string> DiscoveryLimitations { get; init; } = [];
    public JsonObject? TaskPlan { get; init; }
}
