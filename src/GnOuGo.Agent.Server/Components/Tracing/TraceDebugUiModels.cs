using GnOuGo.Agent.Server.Telemetry;
namespace GnOuGo.Agent.Server.Components.Tracing;
public sealed record FlatSpanModel(
    TraceSpanDto Span,
    int Depth,
    double StartMs,
    double EndMs,
    double DurationMs,
    double LeftPercent,
    double WidthPercent);
public sealed record LlmCallModel(
    string OperationName,
    string Model,
    string Provider,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    double DurationMs,
    decimal Cost);
public sealed record RagStepModel(
    string Name,
    string Icon,
    int SpanCount,
    double DurationMs,
    double Percentage);
public sealed record SummaryModel(
    long TotalTokens,
    long PromptTokens,
    long CompletionTokens,
    decimal EstimatedCost,
    double TraceDurationMs,
    int LlmCalls,
    List<LlmCallModel> LlmMetrics,
    List<string> Providers,
    List<string> Models,
    List<RagStepModel> RagSteps);
public enum TraceValueFormat
{
    Plain,
    Json,
    Yaml,
    Markdown
}
public sealed record DisplayValue(TraceValueFormat Format, string Content);

