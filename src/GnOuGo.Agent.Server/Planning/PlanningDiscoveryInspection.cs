using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Planning;

// Read-only display projections of issued requests. These are never planning or approval authority.
internal sealed record DiscoveryToolView(string Id, string Source, string Name, string Status, string Reason,
    bool Requested, bool Used, int ContractBytes);
internal sealed record DiscoverySourceView(string Id, string Name, int Pages, int Tools, bool HasMore, bool Unavailable);
internal sealed record DiscoveryRequestView(string Id, long Revision, int InputTokens, int InputLimit, int PromptBytes,
    IReadOnlyList<DiscoverySourceView> Sources, IReadOnlyList<DiscoveryToolView> Tools, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);
internal sealed record PlanningDiscoveryReport(string SessionId, long Revision, long Calls, int CallLimit,
    IReadOnlyList<DiscoveryRequestView> Requests, IReadOnlyList<string> DiagnosticCodes, IReadOnlyList<string> RequestedOperationIds);

internal static class PlanningDiscoveryInspection
{
    internal static async Task<PlanningDiscoveryReport> ReadAsync(IKeyVaultRecordStore records, string tenant,
        PlanningSession current, bool workflow, CancellationToken ct)
    {
        if (current.Request.TenantId != tenant) throw new PlanningConflictException("Planning session ownership could not be established.");
        var history = new List<PlanningSession>();
        // Designer already retains immutable encrypted revisions. Do not create a
        // second journal or dispatch metadata requests for this display.
        if (!workflow)
            foreach (var record in await records.ListAsync(EfPlanningSessionStore.Collection, tenant, EfPlanningSessionStore.Author, ct))
            {
                if (!record.Key.StartsWith(current.Request.SessionId + ":", StringComparison.Ordinal)) continue;
                var inspection = PlanningSessionHistory.Read(record.Value, tenant, current.Request.SessionId, false, record.UpdatedAt);
                if (inspection.Session is { } saved && saved.Revision <= current.Revision) history.Add(saved);
            }
        history.Add(current);
        return Build(current, history);
    }

    internal static PlanningDiscoveryReport Build(PlanningSession current, IEnumerable<PlanningSession> history)
    {
        var requests = new List<DiscoveryRequestView>();
        var used = Tasks(current.Plan?.Root).Concat(current.Plan?.Groups.SelectMany(g => Tasks(g.Body)) ?? [])
            .Select(t => t.Operation).OfType<string>().ToHashSet(StringComparer.Ordinal);
        HashSet<string> previous = [];
        foreach (var state in history.Where(s => s.Request.TenantId == current.Request.TenantId && s.Request.SessionId == current.Request.SessionId && s.PendingCall is not null)
            .OrderBy(s => s.Revision).DistinctBy(s => s.PendingCall!.Id))
        {
            var request = state.PendingCall!.Request;
            if (request.StructuredOutputSchema is not JsonObject schema) continue;
            JsonObject? context;
            try
            {
                var start = request.Prompt.IndexOf("\n{", StringComparison.Ordinal);
                context = start < 0 ? null : JsonNode.Parse(request.Prompt[(start + 1)..]) as JsonObject;
            }
            catch (JsonException) { continue; }
            if (context?["operations"] is not JsonArray operations || context["coverage"] is not JsonArray coverage) continue;
            var details = operations.OfType<JsonObject>().Where(o => o["id"] is JsonValue)
                .GroupBy(o => o["id"]!.ToString(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var indexed = coverage.OfType<JsonObject>().SelectMany(c => (c["index"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Where(i => i["id"] is JsonValue).Select(i => i["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
            var inspections = (state.Discovery.Inspections ?? []).SelectMany(i => i.OperationIds ?? []).ToHashSet(StringComparer.Ordinal);
            var tools = new List<DiscoveryToolView>();
            foreach (var group in state.Discovery.Pages.SelectMany(p => p.Capabilities).GroupBy(c => c.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var capability = group.First(); var operation = capability.Operation;
                if (operation is null) continue;
                var exact = details.GetValueOrDefault(operation.Id);
                var denied = state.Catalog is { } catalog && (!catalog.AllowedStepTypes.Contains(capability.StepType) || catalog.Policy.DeniedCapabilityIds.Contains(capability.Id));
                var conflict = group.Select(c => c.Version).Distinct(StringComparer.Ordinal).Count() != 1;
                var unavailable = state.Discovery.Limitations.Any(l => l.StartsWith(capability.Id + ":", StringComparison.Ordinal)) &&
                    !state.Discovery.Resolved.Any(c => c.Id == capability.Id && c.Version == capability.Version);
                var status = exact is not null ? exact["contextRole"]?.ToString() == "producer_outputs" ? "Producer outputs" : "Full contract" : indexed.Contains(operation.Id) ? "Index only" :
                    denied ? "Policy excluded" : conflict ? "Version conflict" : unavailable ? "Unavailable" : "Retained, omitted";
                var reason = status switch
                {
                    "Producer outputs" => "Read-only producer output contracts were included; its input contracts were not sent.",
                    "Full contract" => inspections.Contains(operation.Id) ? "Explicitly requested for inspection." : "Included in this issued request.",
                    "Index only" => "Identity and business port names were shown; the full contract was not sent.",
                    "Policy excluded" => "Excluded by the saved planning policy.",
                    "Version conflict" => "Retained versions conflict; they cannot establish one authoritative contract.",
                    "Unavailable" => "The retained receipt reports an unresolved contract.",
                    _ => "Not included in this issued request. Its precise historical ranking/budget reason was not recorded."
                };
                var full = exact ?? Contract(operation);
                tools.Add(new(operation.Id, capability.SourceId, capability.Name, status, reason,
                    inspections.Contains(operation.Id), used.Contains(operation.Id), Bytes(full)));
            }
            string SourceName(string id)
            {
                var ids = state.Discovery.Pages.Where(p => p.SourceId == id).SelectMany(p => p.Capabilities).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                return state.Discovery.Resolved.FirstOrDefault(c => ids.Contains(c.Id) && c.Server is not null)?.Server ?? id;
            }
            var sources = coverage.OfType<JsonObject>().Select(c => new DiscoverySourceView(c["sourceId"]?.ToString() ?? "unknown", SourceName(c["sourceId"]?.ToString() ?? "unknown"),
                Number(c["pagesRead"]), Number(c["indexedCount"]), c["hasMore"]?.ToString() == "true" || c["continuations"] is JsonArray { Count: > 0 },
                c["unavailable"] is not null)).ToArray();
            var currentIds = details.Keys.ToHashSet(StringComparer.Ordinal);
            requests.Add(new(state.PendingCall.Id, state.Revision,
                checked((Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(schema.ToJsonString()) + 2) / 3 + 256),
                state.Request.Generation.MaxInputTokensPerRequest, Encoding.UTF8.GetByteCount(request.Prompt), sources, tools,
                currentIds.Except(previous).Order(StringComparer.Ordinal).ToArray(), previous.Except(currentIds).Order(StringComparer.Ordinal).ToArray()));
            previous = currentIds;
        }
        return new(current.Request.SessionId, current.Revision, Math.Max(current.ModelCalls, current.Usage?.Calls ?? 0),
            Math.Min(current.Request.MaxModelCalls, PlanningBudgetOptions.Parse(current.Request.Options)?.MaxCalls ?? current.Request.MaxModelCalls),
            requests, current.Diagnostics.Select(d => d.Code).Distinct(StringComparer.Ordinal).ToArray(),
            (current.Discovery.Inspections ?? []).SelectMany(i => i.OperationIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static string CopyText(PlanningDiscoveryReport report)
    {
        // Deliberate allowlist: no descriptions, prompts, arguments, schemas,
        // diagnostic messages, local paths, URLs or raw provider responses.
        var text = new StringBuilder().AppendLine($"Discovery report: {Identifier(report.SessionId)}; revision {report.Revision}; calls {report.Calls}/{report.CallLimit}");
        text.AppendLine("Findings: " + string.Join(", ", report.DiagnosticCodes.Select(Identifier)));
        text.AppendLine("Current inspection selection: " + string.Join(", ", report.RequestedOperationIds.Select(Identifier)));
        foreach (var request in report.Requests)
        {
            text.AppendLine($"Request {Identifier(request.Id)}; revision {request.Revision}; input estimate {request.InputTokens}/{request.InputLimit}; prompt bytes {request.PromptBytes}");
            foreach (var source in request.Sources)
                text.AppendLine($"Source {Identifier(source.Id)} ({Identifier(source.Name)}); pages {source.Pages}; discovered {source.Tools}; has more {source.HasMore}; unavailable {source.Unavailable}");
            text.AppendLine("Added contracts: " + string.Join(", ", request.Added.Select(Identifier)));
            text.AppendLine("Removed contracts: " + string.Join(", ", request.Removed.Select(Identifier)));
            foreach (var tool in request.Tools)
                text.AppendLine($"{Identifier(tool.Source)}/{Identifier(tool.Name)} ({Identifier(tool.Id)}): {tool.Status}; requested {tool.Requested}; used in TaskPlan {tool.Used}; contract bytes {tool.ContractBytes}");
        }
        text.AppendLine("Selection scores and precise historical budget-exclusion reasons were not recorded. Inspection grants no execution permission.");
        return text.ToString();
    }

    private static string Identifier(string value) => value.Length is > 0 and <= 256 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains("://", StringComparison.Ordinal) &&
        !new[] { "ghp_", "github_pat_", "sk-", "Bearer", "password", "secret", "token=" }.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase))
        ? value : "[redacted]";
    private static int Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    private static JsonObject Contract(PlanningOperation operation) => new()
    {
        ["id"] = operation.Id, ["description"] = operation.Description,
        ["inputs"] = Ports(operation.Inputs), ["outputs"] = Ports(operation.Outputs)
    };
    private static JsonArray Ports(IEnumerable<OperationPort> ports) => new(ports.Select(p => (JsonNode)new JsonObject
    { ["name"] = p.Name, ["type"] = p.Schema.DeepClone(), ["required"] = p.Required }).ToArray());
    private static IEnumerable<PlanTask> Tasks(TaskScope? scope)
    {
        foreach (var task in scope?.Tasks.Concat(scope.Always) ?? [])
        {
            yield return task;
            foreach (var child in Tasks(task.Body).Concat(Tasks(task.Otherwise)).Concat(task.Branches.SelectMany(Tasks))) yield return child;
        }
    }
}
