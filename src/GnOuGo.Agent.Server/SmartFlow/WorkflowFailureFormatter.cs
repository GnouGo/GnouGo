using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

internal static class WorkflowFailureFormatter
{
    private const int MaxDiagnosticCharacters = 8 * 1024;

    public static WorkflowFailurePresentation Format(WorkflowError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(error.Code))
            builder.Append('[').Append(error.Code.Trim()).Append("] ");
        builder.AppendLine(string.IsNullOrWhiteSpace(error.Message) ? "Workflow execution failed." : error.Message.Trim());

        var unavailable = ReadObjectArrayDeep(error.Details, "unavailable_capabilities");
        if (unavailable.Count > 0)
        {
            builder.AppendLine().AppendLine("Unavailable required operations:");
            foreach (var item in unavailable)
            {
                var id = Sanitize(ReadBoundedString(item, "id", 160) ?? "unnamed_operation", 160);
                var description = Sanitize(ReadBoundedString(item, "description", 1_000) ?? "No description was provided.", 1_000);
                builder.Append("- ").Append(id).Append(": ").AppendLine(description);
            }
        }

        var servers = ReadStringArrayDeep(error.Details, "unavailable_servers");
        if (servers.Count > 0)
        {
            builder.AppendLine().AppendLine("MCP catalogs that could not be discovered:");
            foreach (var server in servers)
                builder.Append("- ").AppendLine(server);
        }

        if (unavailable.Count > 0 || servers.Count > 0)
        {
            builder.AppendLine()
                .Append("Configure a matching discovered capability, alter the requirement, or mark the operation optional before retrying.");
        }

        var inferenceReasons = ReadObjectArrayDeep(error.Details, "incomplete_reasons");
        if (inferenceReasons.Count > 0)
        {
            builder.AppendLine().AppendLine("Why the runtime operation inventory remained incomplete:");
            foreach (var item in inferenceReasons)
            {
                var id = Sanitize(ReadBoundedString(item, "id", 160) ?? "inventory_uncertain", 160);
                var description = Sanitize(ReadBoundedString(item, "description", 1_000) ?? "No clarification was provided.", 1_000);
                builder.Append("- ").Append(id).Append(": ").AppendLine(description);
            }

            builder.AppendLine()
                .Append("Clarify the requested runtime behavior described above and retry. Tool availability and exact capability matching are evaluated separately.");
        }

        var matchingIssues = ReadObjectArrayDeep(error.Details, "matching_issues");
        if (matchingIssues.Count > 0)
        {
            builder.AppendLine().AppendLine("Capability matching issues:");
            foreach (var issue in matchingIssues)
            {
                var id = Sanitize(ReadBoundedString(issue, "operation_id", 160) ?? "unknown_operation", 160);
                var status = Sanitize(ReadBoundedString(issue, "status", 40) ?? "unresolved", 40);
                var description = Sanitize(ReadBoundedString(issue, "description", 1_000) ?? "No description was provided.", 1_000);
                var reason = Sanitize(ReadBoundedString(issue, "reason", 1_000) ?? "No matching reason was provided.", 1_000);
                builder.Append("- ").Append(id).Append(" [").Append(status).Append("]: ").AppendLine(description);
                builder.Append("  Reason: ").AppendLine(reason);

                var candidates = ReadObjectArray(issue, "candidate_capabilities").Take(8).ToArray();
                if (candidates.Length == 0)
                    continue;
                builder.AppendLine("  Candidate capabilities:");
                foreach (var candidate in candidates)
                {
                    var catalogId = Sanitize(ReadBoundedString(candidate, "catalog_id", 160) ?? "unknown_catalog_id", 160);
                    var resolution = Sanitize(ReadBoundedString(candidate, "resolution", 40) ?? "unknown", 40);
                    var server = Sanitize(ReadBoundedString(candidate, "server", 240) ?? "native", 240);
                    var method = Sanitize(ReadBoundedString(candidate, "method", 240) ?? "unknown", 240);
                    var kind = Sanitize(ReadBoundedString(candidate, "kind", 40) ?? resolution, 40);
                    builder.Append("    - ").Append(catalogId).Append(": ").Append(server).Append('/').Append(method)
                        .Append(" (").Append(kind).Append(')');
                    var bindings = FormatCandidateBindings(candidate["request_bindings"] as JsonArray);
                    if (bindings.Length > 0)
                        builder.Append(" selectors: ").Append(bindings);
                    builder.AppendLine();
                }
            }
            builder.AppendLine()
                .Append("Resolve the reported ambiguity by configuring a more specific capability contract or clarifying the runtime intention; catalog IDs and selector values above are the exact discovered candidates.");
        }

        var violatedConstraints = ReadObjectArrayDeep(error.Details, "violated_constraints");
        if (violatedConstraints.Count > 0)
        {
            builder.AppendLine().AppendLine("Locked capability constraint violations:");
            foreach (var violation in violatedConstraints)
            {
                var id = Sanitize(ReadBoundedString(violation, "id", 160) ?? "unknown_constraint", 160);
                var description = Sanitize(ReadBoundedString(violation, "description", 1_000) ?? "No description was provided.", 1_000);
                var server = Sanitize(ReadBoundedString(violation, "server", 240) ?? "unknown_server", 240);
                var method = Sanitize(ReadBoundedString(violation, "method", 240) ?? "unknown_method", 240);
                var kind = Sanitize(ReadBoundedString(violation, "kind", 40) ?? "tool", 40);
                builder.Append("- ").Append(id).Append(": ").AppendLine(description);
                builder.Append("  Denied call: ").Append(server).Append('/').Append(method).Append(" (").Append(kind).Append(')');
                var bindings = FormatCandidateBindings(violation["request_bindings"] as JsonArray);
                if (bindings.Length > 0)
                    builder.Append(" selectors: ").Append(bindings);
                builder.AppendLine();
            }
            builder.AppendLine()
                .Append("Remove the denied call from the generated workflow or correct the exact locked constraint before retrying.");
        }

        var inferencePhase = ReadBoundedStringDeep(error.Details, "inference_phase", 160);
        var inferenceError = ReadBoundedStringDeep(error.Details, "inference_error", 1_000);
        if (!string.IsNullOrWhiteSpace(inferencePhase) || !string.IsNullOrWhiteSpace(inferenceError))
        {
            builder.AppendLine().AppendLine("Capability inference contract failure:");
            if (!string.IsNullOrWhiteSpace(inferencePhase))
                builder.Append("- Phase: ").AppendLine(Sanitize(inferencePhase, 160));
            if (!string.IsNullOrWhiteSpace(inferenceError))
                builder.Append("- Reason: ").AppendLine(Sanitize(inferenceError, 1_000));
            builder.AppendLine()
                .Append("Retry transient provider failures; otherwise use this phase and reason to correct the structured inference contract.");
        }

        var formatted = WorkflowTelemetrySourceFormatter.Format(builder.ToString().Trim(), MaxDiagnosticCharacters);
        return new WorkflowFailurePresentation(
            formatted.Text,
            formatted.Text,
            unavailable.Count,
            servers.Count,
            inferenceReasons.Count,
            matchingIssues.Count,
            violatedConstraints.Count);
    }

    private static IReadOnlyList<JsonObject> ReadObjectArray(JsonNode? details, string property)
        => details is JsonObject obj && obj[property] is JsonArray array
            ? array.OfType<JsonObject>().ToArray()
            : Array.Empty<JsonObject>();

    private static IReadOnlyList<JsonObject> ReadObjectArrayDeep(JsonNode? details, string property)
    {
        foreach (var scope in EnumerateDiagnosticScopes(details))
        {
            var values = ReadObjectArray(scope, property);
            if (values.Count > 0)
                return values;
        }
        return Array.Empty<JsonObject>();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? details, string property)
    {
        if (details is not JsonObject obj || obj[property] is not JsonArray array)
            return Array.Empty<string>();

        return array
            .OfType<JsonValue>()
            .Select(static value => value.TryGetValue<string>(out var text) ? text?.Trim() : null)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Length <= 240 ? value : value[..240])
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringArrayDeep(JsonNode? details, string property)
    {
        foreach (var scope in EnumerateDiagnosticScopes(details))
        {
            var values = ReadStringArray(scope, property);
            if (values.Count > 0)
                return values;
        }
        return Array.Empty<string>();
    }

    private static string? ReadBoundedStringDeep(JsonNode? details, string property, int limit)
    {
        foreach (var scope in EnumerateDiagnosticScopes(details))
        {
            if (scope is JsonObject obj && ReadBoundedString(obj, property, limit) is { } value)
                return value;
        }
        return null;
    }

    private static IEnumerable<JsonNode> EnumerateDiagnosticScopes(JsonNode? details)
    {
        if (details == null)
            yield break;

        var queue = new Queue<(JsonNode Node, int Depth)>();
        queue.Enqueue((details, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            yield return node;
            if (depth >= 4 || node is not JsonObject obj)
                continue;

            foreach (var key in new[] { "terminal_error", "details", "last_error", "main_validation_error" })
            {
                if (obj[key] is JsonObject nested)
                    queue.Enqueue((nested, depth + 1));
            }
        }
    }

    private static string? ReadBoundedString(JsonObject obj, string property, int limit)
    {
        if (obj[property] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + "…";
    }

    private static string Sanitize(string value, int limit)
        => WorkflowTelemetrySourceFormatter.Format(value, limit).Text;

    private static string FormatCandidateBindings(JsonArray? bindings)
    {
        if (bindings == null)
            return string.Empty;
        var values = new List<string>();
        foreach (var binding in bindings.OfType<JsonObject>().Take(8))
        {
            var path = ReadBoundedString(binding, "path", 240);
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var value = binding["value"]?.ToJsonString() ?? "null";
            values.Add($"{Sanitize(path, 240)}={Sanitize(value, 240)}");
        }
        return string.Join(", ", values);
    }
}

internal sealed record WorkflowFailurePresentation(
    string UserMessage,
    string TraceDetails,
    int UnavailableCapabilityCount,
    int UnavailableServerCount,
    int InferenceReasonCount,
    int MatchingIssueCount,
    int ViolatedConstraintCount);
