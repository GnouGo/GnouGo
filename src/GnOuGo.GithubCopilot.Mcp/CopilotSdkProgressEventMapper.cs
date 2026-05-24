using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot.SDK;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CopilotSdkProgressEventMapper
{
    public bool TryMap(SessionEvent sdkEvent, out CodeProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(sdkEvent);

        try
        {
            return TryMap(
                sdkEvent.Type,
                ExtractDataProperties(sdkEvent),
                sdkEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                out progressEvent);
        }
        catch
        {
            progressEvent = null!;
            return false;
        }
    }

    internal static bool TryMap(
        string? eventType,
        IReadOnlyDictionary<string, string?> data,
        string? timestamp,
        out CodeProgressEvent progressEvent)
    {
        progressEvent = null!;
        if (string.IsNullOrWhiteSpace(eventType))
            return false;

        var normalizedType = eventType.Trim().ToLowerInvariant();
        var kind = "sdk_" + normalizedType.Replace('.', '_').Replace('-', '_');
        var level = "thinking";
        string? message = normalizedType switch
        {
            "session.start" => "Copilot session started.",
            "session.resume" => "Copilot session resumed.",
            "session.info" => FirstNonEmpty(data, "Message") ?? "Copilot emitted session information.",
            "session.warning" => FirstNonEmpty(data, "Message") ?? "Copilot emitted a warning.",
            "session.error" => FirstNonEmpty(data, "Message", "ErrorCode", "ErrorType") ?? "Copilot reported a session error.",
            "session.model_change" => BuildModelChangeMessage(data),
            "session.workspace_file_changed" => BuildWorkspaceFileChangedMessage(data),
            "session.compaction_start" => "Copilot started compacting its session context.",
            "session.compaction_complete" => FirstNonEmpty(data, "Error") is { Length: > 0 } error
                ? $"Copilot session compaction failed: {Truncate(error, 240)}"
                : "Copilot completed session context compaction.",
            "session.task_complete" => FirstNonEmpty(data, "Summary") is { Length: > 0 } summary
                ? $"Copilot task complete: {Truncate(summary, 240)}"
                : "Copilot marked the task as complete.",
            "assistant.turn_start" => "Copilot started processing the request.",
            "assistant.intent" => FirstNonEmpty(data, "Intent") is { Length: > 0 } intent
                ? $"Copilot intent: {Truncate(intent, 240)}"
                : "Copilot selected its next intent.",
            "assistant.reasoning" => "Copilot produced a reasoning milestone.",
            "assistant.message_start" => "Copilot started producing a response.",
            "assistant.message" => "Copilot produced a complete response block.",
            "assistant.turn_end" => "Copilot finished processing the request.",
            "assistant.usage" => "Copilot reported usage information.",
            "model.call_failure" => FirstNonEmpty(data, "ErrorMessage") is { Length: > 0 } modelError
                ? $"Copilot model call failed: {Truncate(modelError, 240)}"
                : "Copilot model call failed.",
            "abort" => FirstNonEmpty(data, "Reason") is { Length: > 0 } reason
                ? $"Copilot aborted the turn: {Truncate(reason, 240)}"
                : "Copilot aborted the turn.",
            "tool.user_requested" => BuildToolMessage("Copilot requested tool", data),
            "tool.execution_start" => BuildToolMessage("Copilot started tool", data),
            "tool.execution_progress" => FirstNonEmpty(data, "ProgressMessage") ?? BuildToolMessage("Copilot reported tool progress for", data),
            "tool.execution_complete" => FirstNonEmpty(data, "Error") is { Length: > 0 } toolError
                ? $"Copilot tool execution failed: {Truncate(toolError, 240)}"
                : BuildToolMessage("Copilot completed tool", data),
            "skill.invoked" => BuildSkillMessage(data),
            "subagent.started" => BuildSubagentMessage("Copilot started sub-agent", data),
            "subagent.completed" => BuildSubagentMessage("Copilot completed sub-agent", data),
            "subagent.failed" => FirstNonEmpty(data, "Error") is { Length: > 0 } subagentError
                ? $"Copilot sub-agent failed: {Truncate(subagentError, 240)}"
                : BuildSubagentMessage("Copilot sub-agent failed", data),
            "permission.requested" => BuildToolMessage("Copilot requested permission for", data),
            "permission.completed" => BuildToolMessage("Copilot completed permission flow for", data),
            "mcp.oauth_required" => FirstNonEmpty(data, "ServerName") is { Length: > 0 } oauthServer
                ? $"Copilot requires MCP OAuth for {oauthServer}."
                : "Copilot requires MCP OAuth.",
            "mcp.oauth_completed" => FirstNonEmpty(data, "ServerName") is { Length: > 0 } oauthCompletedServer
                ? $"Copilot completed MCP OAuth for {oauthCompletedServer}."
                : "Copilot completed MCP OAuth.",
            "session.mcp_server_status_changed" => BuildMcpServerStatusMessage(data),
            _ => null
        };

        if (message is null)
            return false;

        if (normalizedType is "session.warning")
            level = "warning";
        else if (normalizedType is "session.error" or "model.call_failure" or "abort" or "subagent.failed")
            level = "error";
        else if (normalizedType is "session.info" or "session.task_complete" or "assistant.message" or "assistant.turn_end" or "tool.execution_complete")
            level = "info";

        progressEvent = new CodeProgressEvent(
            Kind: kind,
            Level: level,
            Message: Truncate(message, 1000),
            Timestamp: ParseTimestamp(timestamp),
            File: FirstNonEmpty(data, "Path"));
        return true;
    }

    private static IReadOnlyDictionary<string, string?> ExtractDataProperties(SessionEvent sdkEvent)
    {
        var root = JsonNode.Parse(sdkEvent.ToJson()) as JsonObject;
        if (root is null || !TryGetProperty(root, "data", out var dataNode) || dataNode is not JsonObject data)
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in data)
        {
            values[property.Key] = CoerceJsonString(property.Value);
        }

        return values;
    }

    private static bool TryGetProperty(JsonObject json, string name, out JsonNode? value)
    {
        foreach (var property in json)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? CoerceJsonString(JsonNode? value)
    {
        if (value is null)
            return null;

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
                return text;
            if (jsonValue.TryGetValue<DateTimeOffset>(out var dateTimeOffset))
                return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            if (jsonValue.TryGetValue<DateTime>(out var dateTime))
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            if (jsonValue.TryGetValue<bool>(out var boolean))
                return boolean.ToString(CultureInfo.InvariantCulture);
            if (jsonValue.TryGetValue<decimal>(out var decimalValue))
                return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string?> data, params string[] names)
    {
        foreach (var name in names)
        {
            if (data.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static DateTimeOffset ParseTimestamp(string? timestamp)
        => DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.UtcNow;

    private static string BuildToolMessage(string prefix, IReadOnlyDictionary<string, string?> data)
    {
        var toolName = FirstNonEmpty(data, "McpToolName", "ToolName");
        var serverName = FirstNonEmpty(data, "McpServerName");
        return toolName is null
            ? $"{prefix} a tool."
            : serverName is null
                ? $"{prefix} {toolName}."
                : $"{prefix} {serverName}/{toolName}.";
    }

    private static string BuildSkillMessage(IReadOnlyDictionary<string, string?> data)
    {
        var name = FirstNonEmpty(data, "Name");
        var pluginName = FirstNonEmpty(data, "PluginName");
        return name is null
            ? "Copilot invoked a skill."
            : pluginName is null
                ? $"Copilot invoked skill {name}."
                : $"Copilot invoked skill {pluginName}/{name}.";
    }

    private static string BuildSubagentMessage(string prefix, IReadOnlyDictionary<string, string?> data)
    {
        var displayName = FirstNonEmpty(data, "AgentDisplayName", "AgentName");
        return displayName is null ? $"{prefix}." : $"{prefix} {displayName}.";
    }

    private static string BuildModelChangeMessage(IReadOnlyDictionary<string, string?> data)
    {
        var model = FirstNonEmpty(data, "Model");
        return model is null ? "Copilot changed model settings." : $"Copilot switched to model {model}.";
    }

    private static string BuildWorkspaceFileChangedMessage(IReadOnlyDictionary<string, string?> data)
    {
        var path = FirstNonEmpty(data, "Path");
        return path is null ? "Copilot changed a workspace file." : $"Copilot changed {path}.";
    }

    private static string BuildMcpServerStatusMessage(IReadOnlyDictionary<string, string?> data)
    {
        var serverName = FirstNonEmpty(data, "ServerName");
        var status = FirstNonEmpty(data, "Status");
        return (serverName, status) switch
        {
            ({ }, { }) => $"Copilot MCP server {serverName} status changed to {status}.",
            ({ }, null) => $"Copilot MCP server {serverName} status changed.",
            _ => "Copilot MCP server status changed."
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }
}

