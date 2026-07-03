using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.Mcp.Core;

public static class McpToolErrorNormalizer
{
    private const string ToolErrorCode = "MCP_TOOL_ERROR";
    private const string DefaultToolErrorMessage = "MCP tool returned an error.";

    private static readonly HashSet<string> ErrorStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "error",
        "failed",
        "failure"
    };

    public static void AddGnOuGoToolErrorNormalizer(this McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Filters.Request.CallToolFilters.Add(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            return Normalize(result);
        });
    }

    public static CallToolResult Normalize(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsError == true)
        {
            EnsureStructuredToolError(result);
            return result;
        }

        if (IsFailure(result.StructuredContent))
        {
            result.IsError = true;
            return result;
        }

        if (result.Content is not { Count: 1 } || result.Content[0] is not TextContentBlock textBlock)
            return result;

        var text = textBlock.Text;
        if (string.IsNullOrWhiteSpace(text))
            return result;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (IsFailure(document.RootElement))
                result.IsError = true;
        }
        catch (JsonException)
        {
            // Plain text can legitimately mention errors in successful diagnostic output.
        }

        return result;
    }

    private static void EnsureStructuredToolError(CallToolResult result)
    {
        if (HasStructuredContent(result.StructuredContent))
            return;

        result.StructuredContent = CreateToolErrorEnvelope(ExtractErrorMessage(result));
    }

    private static bool HasStructuredContent(JsonElement? element)
        => element.HasValue && element.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;

    private static JsonElement CreateToolErrorEnvelope(string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WriteBoolean("ok", false);
            writer.WriteString("error_code", ToolErrorCode);
            writer.WriteString("error_message", message);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string ExtractErrorMessage(CallToolResult result)
    {
        if (result.Content is not { Count: > 0 })
            return DefaultToolErrorMessage;

        var messages = new List<string>();
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
                messages.Add(textBlock.Text.Trim());
        }

        return messages.Count == 0
            ? DefaultToolErrorMessage
            : string.Join(Environment.NewLine, messages);
    }

    private static bool IsFailure(JsonElement? element)
        => element.HasValue && IsFailure(element.Value);

    private static bool IsFailure(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetProperty(element, "success", out var success) && success.ValueKind == JsonValueKind.False)
            return true;

        if (TryGetProperty(element, "ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return true;

        if (TryGetProperty(element, "status", out var status)
            && status.ValueKind == JsonValueKind.String
            && ErrorStatuses.Contains(status.GetString() ?? ""))
        {
            return true;
        }

        if (HasNonEmptyString(element, "error_code") || HasNonEmptyString(element, "errorCode"))
            return true;

        if (HasNonEmptyString(element, "error_message") || HasNonEmptyString(element, "errorMessage"))
            return true;

        if (TryGetProperty(element, "error", out var error)
            && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            && (error.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(error.GetString())))
        {
            return true;
        }

        return LooksLikeCodeMessageError(element);
    }

    private static bool LooksLikeCodeMessageError(JsonElement element)
    {
        if (!HasNonEmptyString(element, "code") || !HasNonEmptyString(element, "message"))
            return false;

        var propertyCount = 0;
        foreach (var property in element.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount > 3)
                return false;
        }

        return true;
    }

    private static bool HasNonEmptyString(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString());

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var current in element.EnumerateObject())
        {
            if (string.Equals(current.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = current.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}
