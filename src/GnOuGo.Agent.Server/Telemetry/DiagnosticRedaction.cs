using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Telemetry;

internal static partial class DiagnosticRedaction
{
    internal static string Redact(string value)
    {
        try
        {
            if (JsonNode.Parse(value) is { } node) { RedactNode(node); return node.ToJsonString(); }
        }
        catch (JsonException) { }
        return RedactText(value);
    }
    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (SecretName().IsMatch(key) && obj[key] is JsonValue) obj[key] = "[redacted]";
                else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = RedactText(text);
                else if (obj[key] is { } child) RedactNode(child);
            }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = RedactText(text);
                else if (array[index] is { } child) RedactNode(child);
    }
    private static string RedactText(string value)
    {
        value = JsonSecret().Replace(value, "$1\"[redacted]\"");
        value = Bearer().Replace(value, "$1[redacted]");
        value = QuerySecret().Replace(value, "$1[redacted]");
        value = HeaderSecret().Replace(value, "$1[redacted]");
        value = UrlCredentials().Replace(value, "$1[redacted]@");
        value = Pem().Replace(value, "[redacted private key]");
        return value;
    }
    [GeneratedRegex("^(?:api[_-]?key|apiKey|authorization|cookie|set-cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|clientSecret|privateKeyPem|password|secret|token)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
    [GeneratedRegex("(\\\"(?:api[_-]?key|apiKey|authorization|cookie|set-cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|clientSecret|privateKeyPem|password|secret|token)\\\"\\s*:\\s*)\\\"(?:\\\\.|[^\\\"\\\\])*\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();
    [GeneratedRegex("(Bearer\\s+)[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();
    [GeneratedRegex("([?&](?:api[_-]?key|access[_-]?token|client[_-]?secret|token|secret|password|sig|signature|key)=)[^&\\s\\\"]+", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecret();
    [GeneratedRegex("(^[ \\t]*(?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key)[ \\t]*:[ \\t]*)[^\\r\\n]+", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeaderSecret();
    [GeneratedRegex("(https?://)[^/\\s:@]+:[^/\\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
    [GeneratedRegex("-----BEGIN (?:RSA |EC )?PRIVATE KEY-----[\\s\\S]*?-----END (?:RSA |EC )?PRIVATE KEY-----")]
    private static partial Regex Pem();
}
