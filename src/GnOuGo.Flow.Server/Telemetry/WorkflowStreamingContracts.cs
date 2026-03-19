using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Server.Telemetry;

public sealed record WorkflowStreamEvent(string Type, object Data)
{
	public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record WorkflowUsageSummary(
	long InputTokens,
	long OutputTokens,
	long TotalTokens,
	decimal? EstimatedCostUsd,
	int TokenizedStepCount,
	int PricedStepCount,
	IReadOnlyList<string> Models);

public sealed record StepUsageSummary(
	string? Model,
	string? System,
	long? InputTokens,
	long? OutputTokens,
	long? TotalTokens,
	decimal? EstimatedCostUsd,
	string? FinishReason);

public sealed record WorkflowStartedStreamData(
	string WorkflowName,
	string? DocumentName,
	JsonNode? Inputs);

public sealed record WorkflowCompletedStreamData(
	bool Success,
	int StepsExecuted,
	double DurationMs,
	string? ErrorCode,
	string? ErrorMessage,
	WorkflowUsageSummary Summary);

public sealed record WorkflowSummaryStreamData(WorkflowUsageSummary Summary);

public sealed record StepStartedStreamData(
	string StepId,
	string StepType,
	int CallDepth,
	JsonNode? Input);

public sealed record StepTelemetryEventStreamData(
	string StepId,
	string StepType,
	int CallDepth,
	string Name,
	IReadOnlyDictionary<string, object?> Attributes,
	string? ContentText,
	JsonNode? ContentJson);

public sealed record StepCompletedStreamData(
	string StepId,
	string StepType,
	int CallDepth,
	string Status,
	double DurationMs,
	JsonNode? Output,
	string? ErrorCode,
	string? ErrorMessage,
	IReadOnlyDictionary<string, object?> Attributes,
	StepUsageSummary Usage);

