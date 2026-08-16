namespace GnOuGo.Flow.Core.Runtime;

public sealed class McpRealtimeProgressEvent
{
    public string? ServerName { get; init; }
    public string? MethodName { get; init; }
    public string? Kind { get; init; }
    public string? CorrelationId { get; init; }
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? StepType { get; init; }
    public string? EventKind { get; init; }
    public string? Level { get; init; }
    public string Message { get; init; } = "";
    public string? File { get; init; }
    public string? Timestamp { get; init; }
}

public enum McpHumanInputSignalPhase
{
    Waiting,
    Resumed,
    Refused,
    Cancelled
}

public sealed record McpHumanInputSignal(
    McpCorrelationContext Correlation,
    HumanInputRequest Request,
    McpHumanInputSignalPhase Phase);
