using GnOuGo.Agent.Server.Telemetry;
namespace GnOuGo.Agent.Server.Components.Tracing;
public sealed record FlatSpanModel(
    TraceSpanDto Span,
    int Depth,
    int ChildCount,
    double StartMs,
    double EndMs,
    double DurationMs,
    double LeftPercent,
    double WidthPercent);
public sealed record LlmCallModel(
    string OperationName,
    string Model,
    string Provider,
    long? PromptTokens,
    long? CompletionTokens,
    long? TotalTokens,
    double DurationMs,
    decimal? Cost,
    string? Currency);
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
    List<RagStepModel> RagSteps)
{
    public bool UsageComplete => LlmMetrics.All(c => c.TotalTokens.HasValue);
    public string CostSummary => LlmMetrics.Count == 0 || LlmMetrics.Any(c => !c.Cost.HasValue || c.Currency is null)
        || LlmMetrics.Select(c => c.Currency).Distinct().Count() != 1 ? "unknown / incomplete"
        : EstimatedCost.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + " " + LlmMetrics[0].Currency;
}
public enum TraceValueFormat
{
    Plain,
    Json,
    Yaml,
    Markdown
}
public sealed record DisplayValue(TraceValueFormat Format, string Content);
