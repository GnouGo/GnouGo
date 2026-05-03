using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GnOuGo.Code.Mcp;

internal sealed record CodeMcpTraceContext(
    string? TraceParent,
    string? TraceState,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    string? CorrelationId,
    string? RunId,
    string? StepId,
    string? StepType,
    string? McpServer,
    string? McpMethod,
    string? McpKind)
{
    public static CodeMcpTraceContext? Capture(CodeMcpTraceContextAccessor? accessor = null)
        => FromActivity(Activity.Current)
           ?? accessor?.Current
           ?? FromEnvironment();

    public static CodeMcpTraceContext? FromActivity(Activity? activity)
    {
        if (activity is null)
            return null;

        return new CodeMcpTraceContext(
            TraceParent: activity.Id,
            TraceState: activity.TraceStateString,
            TraceId: activity.TraceId.ToString(),
            SpanId: activity.SpanId.ToString(),
            ParentSpanId: activity.ParentSpanId.ToString(),
            CorrelationId: Environment.GetEnvironmentVariable("GNouGo__CorrelationId"),
            RunId: Environment.GetEnvironmentVariable("GNouGo__RunId"),
            StepId: Environment.GetEnvironmentVariable("GNouGo__StepId"),
            StepType: Environment.GetEnvironmentVariable("GNouGo__StepType"),
            McpServer: Environment.GetEnvironmentVariable("GNouGo__McpServer"),
            McpMethod: Environment.GetEnvironmentVariable("GNouGo__McpMethod"),
            McpKind: Environment.GetEnvironmentVariable("GNouGo__McpKind"));
    }

    public static CodeMcpTraceContext? FromEnvironment()
    {
        var traceParent = FirstNonEmpty(
            Environment.GetEnvironmentVariable("GNouGo__TraceParent"),
            Environment.GetEnvironmentVariable("TRACEPARENT"));
        var traceState = FirstNonEmpty(
            Environment.GetEnvironmentVariable("GNouGo__TraceState"),
            Environment.GetEnvironmentVariable("TRACESTATE"));
        var traceId = Environment.GetEnvironmentVariable("GNouGo__TraceId");
        var spanId = Environment.GetEnvironmentVariable("GNouGo__SpanId");

        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(spanId))
        {
            var parsed = ParseTraceParent(traceParent);
            traceId = FirstNonEmpty(traceId, parsed.TraceId);
            spanId = FirstNonEmpty(spanId, parsed.SpanId);
        }

        var context = new CodeMcpTraceContext(
            TraceParent: traceParent,
            TraceState: traceState,
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: spanId,
            CorrelationId: Environment.GetEnvironmentVariable("GNouGo__CorrelationId"),
            RunId: Environment.GetEnvironmentVariable("GNouGo__RunId"),
            StepId: Environment.GetEnvironmentVariable("GNouGo__StepId"),
            StepType: Environment.GetEnvironmentVariable("GNouGo__StepType"),
            McpServer: Environment.GetEnvironmentVariable("GNouGo__McpServer"),
            McpMethod: Environment.GetEnvironmentVariable("GNouGo__McpMethod"),
            McpKind: Environment.GetEnvironmentVariable("GNouGo__McpKind"));

        return context.HasAnyValue ? context : null;
    }

    public static CodeMcpTraceContext? FromMcpMeta(JsonObject? meta)
    {
        if (meta is null)
            return null;

        var gnougo = meta["gnougo"] as JsonObject;
        var traceParent = FirstNonEmpty(ReadString(gnougo, "traceparent"), ReadString(meta, "traceparent"));
        var traceState = FirstNonEmpty(ReadString(gnougo, "tracestate"), ReadString(meta, "tracestate"));
        var traceId = ReadString(gnougo, "traceId");
        var spanId = ReadString(gnougo, "spanId");
        var parentSpanId = ReadString(gnougo, "parentSpanId");

        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(parentSpanId))
        {
            var parsed = ParseTraceParent(traceParent);
            traceId = FirstNonEmpty(traceId, parsed.TraceId);
            parentSpanId = FirstNonEmpty(parentSpanId, parsed.SpanId);
        }

        var context = new CodeMcpTraceContext(
            TraceParent: traceParent,
            TraceState: traceState,
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            CorrelationId: ReadString(gnougo, "correlationId"),
            RunId: ReadString(gnougo, "runId"),
            StepId: ReadString(gnougo, "stepId"),
            StepType: ReadString(gnougo, "stepType"),
            McpServer: ReadString(gnougo, "mcpServer"),
            McpMethod: ReadString(gnougo, "mcpMethod"),
            McpKind: ReadString(gnougo, "mcpKind"));

        return context.HasAnyValue ? context : null;
    }

    public JsonObject ToMcpMeta()
    {
        var gnougo = new JsonObject();
        Add(gnougo, "traceparent", TraceParent);
        Add(gnougo, "tracestate", TraceState);
        Add(gnougo, "traceId", TraceId);
        Add(gnougo, "spanId", SpanId);
        Add(gnougo, "parentSpanId", ParentSpanId);
        Add(gnougo, "correlationId", CorrelationId);
        Add(gnougo, "runId", RunId);
        Add(gnougo, "stepId", StepId);
        Add(gnougo, "stepType", StepType);
        Add(gnougo, "mcpServer", McpServer);
        Add(gnougo, "mcpMethod", McpMethod);
        Add(gnougo, "mcpKind", McpKind);

        var meta = new JsonObject { ["gnougo"] = gnougo };
        Add(meta, "traceparent", TraceParent);
        Add(meta, "tracestate", TraceState);
        return meta;
    }

    public Dictionary<string, string> ToHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(headers, "traceparent", TraceParent);
        Add(headers, "tracestate", TraceState);
        Add(headers, "x-gnougo-trace-id", TraceId);
        Add(headers, "x-gnougo-span-id", SpanId);
        Add(headers, "x-gnougo-parent-span-id", ParentSpanId);
        Add(headers, "x-gnougo-correlation-id", CorrelationId);
        Add(headers, "x-gnougo-run-id", RunId);
        Add(headers, "x-gnougo-step-id", StepId);
        Add(headers, "x-gnougo-step-type", StepType);
        Add(headers, "x-gnougo-mcp-server", McpServer);
        Add(headers, "x-gnougo-mcp-method", McpMethod);
        Add(headers, "x-gnougo-mcp-kind", McpKind);
        return headers;
    }

    public Dictionary<string, string> ToEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(env, "TRACEPARENT", TraceParent);
        Add(env, "TRACESTATE", TraceState);
        Add(env, "GNouGo__TraceParent", TraceParent);
        Add(env, "GNouGo__TraceState", TraceState);
        Add(env, "GNouGo__TraceId", TraceId);
        Add(env, "GNouGo__SpanId", SpanId);
        Add(env, "GNouGo__CorrelationId", CorrelationId);
        Add(env, "GNouGo__RunId", RunId);
        Add(env, "GNouGo__StepId", StepId);
        Add(env, "GNouGo__StepType", StepType);
        Add(env, "GNouGo__McpServer", McpServer);
        Add(env, "GNouGo__McpMethod", McpMethod);
        Add(env, "GNouGo__McpKind", McpKind);
        Add(env, "OTEL_PROPAGATORS", "tracecontext,baggage");
        return env;
    }

    private bool HasAnyValue
        => !string.IsNullOrWhiteSpace(TraceParent)
           || !string.IsNullOrWhiteSpace(TraceId)
           || !string.IsNullOrWhiteSpace(SpanId)
           || !string.IsNullOrWhiteSpace(CorrelationId)
           || !string.IsNullOrWhiteSpace(RunId)
           || !string.IsNullOrWhiteSpace(StepId);

    private static (string? TraceId, string? SpanId) ParseTraceParent(string? traceParent)
    {
        if (string.IsNullOrWhiteSpace(traceParent))
            return default;

        var parts = traceParent.Split('-');
        return parts.Length >= 4 ? (parts[1], parts[2]) : default;
    }

    private static string? ReadString(JsonObject? obj, string name)
        => obj is not null && obj.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static void Add(JsonObject obj, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            obj[name] = value;
    }

    private static void Add(Dictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values[name] = value;
    }
}

internal sealed class CodeMcpTraceContextAccessor
{
    private readonly AsyncLocal<CodeMcpTraceContext?> _current = new();

    public CodeMcpTraceContext? Current => _current.Value;

    public IDisposable Push(CodeMcpTraceContext? context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CodeMcpTraceContextAccessor _accessor;
        private readonly CodeMcpTraceContext? _previous;
        private bool _disposed;

        public Scope(CodeMcpTraceContextAccessor accessor, CodeMcpTraceContext? previous)
        {
            _accessor = accessor;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _accessor._current.Value = _previous;
            _disposed = true;
        }
    }
}

