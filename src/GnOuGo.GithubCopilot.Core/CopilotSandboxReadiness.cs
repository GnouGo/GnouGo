using System.Text.Json;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Device policy readiness only; configured policy does not prove session enforcement.</summary>
public enum CopilotSandboxReadiness { NotConfigured, Configured, Invalid, Unavailable }

internal static class CopilotSandboxPolicy
{
    internal static CopilotSandboxReadiness Read(string? settingsJson, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error)) return CopilotSandboxReadiness.Invalid;
        if (string.IsNullOrWhiteSpace(settingsJson)) return CopilotSandboxReadiness.NotConfigured;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || DuplicateKeys(root)) return CopilotSandboxReadiness.Invalid;
            if (!root.TryGetProperty("sandbox", out var sandbox)) return CopilotSandboxReadiness.NotConfigured;
            if (sandbox.ValueKind != JsonValueKind.Object || DuplicateKeys(sandbox)) return CopilotSandboxReadiness.Invalid;
            var enabled = Flag(sandbox, "enabled"); var required = Flag(sandbox, "failIfUnavailable");
            if (enabled is null || required is null) return CopilotSandboxReadiness.Invalid;
            return enabled.Value && required.Value ? CopilotSandboxReadiness.Configured : CopilotSandboxReadiness.NotConfigured;
        }
        catch (JsonException) { return CopilotSandboxReadiness.Invalid; }
    }
    private static bool? Flag(JsonElement parent, string name)
        => !parent.TryGetProperty(name, out var value) ? false : value.ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static bool DuplicateKeys(JsonElement value)
        => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count();
}
