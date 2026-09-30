using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Server.Telemetry;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkflowRunRequest))]
[JsonSerializable(typeof(WorkflowRunResponse))]
[JsonSerializable(typeof(WorkflowHumanAnswer))]
[JsonSerializable(typeof(WorkflowRunCommand))]
[JsonSerializable(typeof(ServerError))]
[JsonSerializable(typeof(ServerHealth))]
[JsonSerializable(typeof(WorkflowStreamEvent))]
[JsonSerializable(typeof(WorkflowResultStreamData))]
[JsonSerializable(typeof(WorkflowStartedStreamData))]
[JsonSerializable(typeof(WorkflowCompletedStreamData))]
[JsonSerializable(typeof(WorkflowSummaryStreamData))]
[JsonSerializable(typeof(StepStartedStreamData))]
[JsonSerializable(typeof(StepCompletedStreamData))]
[JsonSerializable(typeof(StepTelemetryEventStreamData))]
[JsonSerializable(typeof(WorkflowPhaseStartedStreamData))]
[JsonSerializable(typeof(WorkflowPhaseCompletedStreamData))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(string[]))]
internal partial class FlowServerJsonContext : JsonSerializerContext;
public sealed record ServerError(string Error);
public sealed record ServerHealth(string Status, DateTimeOffset Timestamp);
public sealed record WorkflowResultStreamData(WorkflowRunResponse Response, WorkflowUsageSummary Summary);
