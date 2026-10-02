using System.Text.Json.Nodes;

namespace GnOuGo.Mcp.Core;

/// <summary>Producer-declared planning effects. These declarations never grant execution permission.</summary>
public static class McpEffectMetadata
{
    public const string Read = """{"effect":{"version":1,"kind":"read"}}""";
    public const string Write = """{"effect":{"version":1,"kind":"write"}}""";
    public const string Execute = """{"effect":{"version":1,"kind":"execute"}}""";
    public const string Lifecycle = """{"effect":{"version":1,"kind":"lifecycle"}}""";

    /// <summary>Absent declarations preserve the standard read-only hint fallback; invalid declarations fail discovery.</summary>
    public static string Resolve(JsonNode? metadata, bool? readOnlyHint = null)
    {
        if (metadata is not JsonObject meta || !meta.TryGetPropertyValue("gnougo", out var node))
            return readOnlyHint == true ? "read" : "unknown";
        if (node is not JsonObject gnougo) throw Invalid();
        if (!gnougo.TryGetPropertyValue("effect", out var declaration)) return readOnlyHint == true ? "read" : "unknown";
        if (declaration is not JsonObject effect || effect.Count != 2 ||
            effect["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1 ||
            effect["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var value) ||
            value is not ("read" or "write" or "execute" or "lifecycle" or "none") ||
            readOnlyHint == true && value is not ("read" or "none")) throw Invalid();
        return value;
    }

    private static ArgumentException Invalid() => new("Invalid or contradictory gnougo.effect metadata. Expected version 1 and a declared planning effect.");
}
