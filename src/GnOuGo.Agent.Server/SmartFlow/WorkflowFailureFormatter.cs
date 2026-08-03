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

        var unavailable = ReadObjectArray(error.Details, "unavailable_capabilities");
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

        var servers = ReadStringArray(error.Details, "unavailable_servers");
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

        var formatted = WorkflowTelemetrySourceFormatter.Format(builder.ToString().Trim(), MaxDiagnosticCharacters);
        return new WorkflowFailurePresentation(
            formatted.Text,
            formatted.Text,
            unavailable.Count,
            servers.Count);
    }

    private static IReadOnlyList<JsonObject> ReadObjectArray(JsonNode? details, string property)
        => details is JsonObject obj && obj[property] is JsonArray array
            ? array.OfType<JsonObject>().ToArray()
            : Array.Empty<JsonObject>();

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

    private static string? ReadBoundedString(JsonObject obj, string property, int limit)
    {
        if (obj[property] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + "…";
    }

    private static string Sanitize(string value, int limit)
        => WorkflowTelemetrySourceFormatter.Format(value, limit).Text;
}

internal sealed record WorkflowFailurePresentation(
    string UserMessage,
    string TraceDetails,
    int UnavailableCapabilityCount,
    int UnavailableServerCount);
