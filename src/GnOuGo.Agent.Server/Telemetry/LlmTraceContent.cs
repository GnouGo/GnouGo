using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Telemetry;

// Diagnostic contracts only. Never used to resume, authorize or account for inference.
public sealed class LlmTraceContent
{
    public string TenantId { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string SpanId { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Journal { get; set; }
    public string? RequestKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string InputStatus { get; set; } = "unavailable";
    public string OutputStatus { get; set; } = "pending";
    public int InputBytes { get; set; }
    public int? OutputBytes { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
}

public sealed record LlmJournalCall(string Key, DateTimeOffset CreatedAt, string Provider, string Model, string Status, long? InputTokens = null, long? OutputTokens = null);
public sealed record LlmContentPart(string Name, int Bytes)
{
    public int EstimatedTokens => (Bytes + 3) / 4;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LlmTraceContent))]
internal partial class LlmTraceJsonContext : JsonSerializerContext;
