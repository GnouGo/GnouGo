using GnOuGo.Flow.Core.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>One logical call, identified by its exact owner and request. HTTP attempts never create calls.</summary>
public sealed record LlmCallProjection(string TenantId, string SessionId, bool Workflow, string? RequestId, string? JournalKey,
    DateTimeOffset? CreatedAt, string? Provider, string? Model, string? Phase, string? BlockId,
    string ResponseStatus, string TechnicalValidation, string BusinessReview, long? InputTokens, long? OutputTokens,
    TraceSpanDto? Span = null, string? TraceId = null)
{
    public string? EvidenceVersion { get; init; }
    public string Identity => string.Join('|', TenantId, Workflow ? "flow" : "agent", SessionId,
        RequestId ?? ("span:" + TraceId + ":" + Span?.SpanId));
    public string Source => JournalKey is null ? "OpenTelemetry" : Span is null ? "Encrypted journal — telemetry absent" : "OpenTelemetry + encrypted journal";
    public static string? Value(TraceSpanDto span, params string[] names) => names.Select(name => span.Attributes.GetValueOrDefault(name)?.ToString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    public static long? Number(TraceSpanDto span, params string[] names) => long.TryParse(Value(span, names), out var value) ? value : null;
    public static List<TraceSpanDto> Calls(TraceGroupDto trace)
    {
        bool Candidate(TraceSpanDto span) => span.Attributes.ContainsKey("gnougo.llm.call_id")
            || span.Kind == 3 && span.Attributes.ContainsKey("gen_ai.operation.name") && span.Attributes.ContainsKey("gen_ai.request.model");
        bool HasModelChild(TraceSpanDto span)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { span.SpanId };
            var queue = new Queue<string>(); queue.Enqueue(span.SpanId);
            while (queue.TryDequeue(out var parent))
                foreach (var child in trace.Spans.Where(s => s.ParentSpanId == parent))
                    if (visited.Add(child.SpanId)) { if (Candidate(child)) return true; queue.Enqueue(child.SpanId); }
            return false;
        }
        var byId = trace.Spans.GroupBy(s => s.SpanId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool HasCapturedParent(TraceSpanDto span)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (span.ParentSpanId is { } parent && seen.Add(parent) && byId.TryGetValue(parent, out var ancestor))
            {
                if (ancestor.Attributes.ContainsKey("gnougo.llm.call_id")) return true;
                span = ancestor;
            }
            return false;
        }
        return trace.Spans.Where(s => Candidate(s) && !HasCapturedParent(s) && (s.Attributes.ContainsKey("gnougo.llm.call_id") || !HasModelChild(s)))
            .OrderBy(s => s.StartUtc).ThenBy(s => s.SpanId, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<LlmCallProjection> Join(IEnumerable<LlmCallProjection> journal, IEnumerable<TraceGroupDto> traces,
        string tenant, string? session, bool workflow)
    {
        var result = journal.ToDictionary(c => c.Identity, StringComparer.Ordinal);
        foreach (var trace in traces)
            foreach (var span in Calls(trace))
            {
                string? V(params string[] keys) => Value(span, keys);
                var owner = V("tenant.id");
                if (owner is not null && owner != tenant) continue;
                var spanSession = V(workflow ? "gnougo-flow.plan.session_id" : "gnougo.planning.session_id");
                var request = V("gnougo.llm.request_id");
                if (session is not null && (spanSession != session || request?.StartsWith(session + ":", StringComparison.Ordinal) != true)) continue;
                var candidate = new LlmCallProjection(tenant, session ?? "", workflow, request, null, span.StartUtc,
                    V("gen_ai.provider.name", "gen_ai.system"), V("gen_ai.request.model"), V("gnougo.llm.stage"), V("gnougo.planning.block_id"),
                    LlmInspectionLabels.State(V("gnougo.llm.status") ?? "Outcome unavailable"), "Unavailable", "Unavailable",
                    Number(span, "gen_ai.usage.input_tokens", "gen_ai.usage.prompt_tokens"),
                    Number(span, "gen_ai.usage.output_tokens", "gen_ai.usage.completion_tokens"), span, V("gnougo.llm.trace_id") ?? trace.TraceId);
                if (result.TryGetValue(candidate.Identity, out var retained))
                    result[candidate.Identity] = retained with { Span = retained.Span ?? span, TraceId = retained.TraceId ?? candidate.TraceId };
                else result[candidate.Identity] = candidate;
            }
        return result.Values.OrderBy(c => c.CreatedAt).ThenBy(c => c.RequestId, StringComparer.Ordinal).ToArray();
    }
}
public sealed record LlmCallCursor(KeyVaultRecordCursor? Requests = null, KeyVaultRecordCursor? Receipts = null,
    KeyVaultRecordCursor? Http = null, long Event = 0);
public sealed record LlmCallPage(IReadOnlyList<LlmCallProjection> Calls, LlmCallCursor Cursor, bool HasMore);

public static class LlmInspectionLabels
{
    public static string Phase(string? phase) => phase switch
    {
        "construction_intent" or "requirements" or "workflow.plan.intent" => "Understanding",
        "construction_select" => "Business selection", "construction_implementation" => "Implementation selection",
        "construction_discovery" or "discovery" or "workflow.plan.choices" => "Capability / context discovery",
        "construction_draft" => "Fragment generation", "technical_validation" => "Technical validation",
        "construction_local_judge" => "Local business review", "replan" or "replanning" or "workflow.plan.repair" => "Correction",
        "construction_global_judge" => "Global business review", "compilation" or "validation" => "Compilation",
        "ready_for_approval" => "Ready to approve", null => "Phase unavailable", _ => phase
    };

    public static string State(string state) => state switch
    {
        "active" => "Active", "waiting" => "Waiting", "received" => "Response received",
        "succeeded" or "completed" => "Completed", "correction" => "Correction required",
        "blocked" => "Blocked", "cancelled" => "Cancelled", "skipped" => "Skipped",
        "invalid" => "Invalid", "failed" => "Failed", _ => state
    };
}
