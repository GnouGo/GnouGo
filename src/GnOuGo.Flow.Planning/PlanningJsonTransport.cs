using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning;

internal static class PlanningJsonTransport
{
    internal static string ValueScope(string nodeKey) => "scope_" + PlanningGraphCompiler.Fingerprint(nodeKey)[..8] + "_";

    internal static PlanningValue Literal(JsonNode? json) => json switch
    {
        null => new(),
        JsonObject obj => new() { Kind = "object", Members = obj.Select(p => new PlanningMember(p.Key, Literal(p.Value))).ToList() },
        JsonArray array => new() { Kind = "array", Items = array.Select(Literal).ToList() },
        JsonValue value when value.TryGetValue<string>(out var text) => new() { Kind = "string", Text = text },
        JsonValue value when value.TryGetValue<bool>(out var boolean) => new() { Kind = "boolean", Boolean = boolean },
        JsonValue value => new() { Kind = "number", Number = value.GetValue<decimal>() },
        _ => throw new InvalidOperationException("Unsupported literal.")
    };

    public static int EstimateInputTokens(string prompt, JsonObject schema) => checked((Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schema.ToJsonString()) + 2) / 3 + 256);

    internal static void PruneDefinitions(JsonObject schema)
    {
        var definitions = schema["$defs"]!.AsObject(); var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (value is not JsonObject obj) return;
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var name) && name.StartsWith("#/$defs/", StringComparison.Ordinal))
            { var key = name[8..]; if (used.Add(key)) Visit(definitions[key]); }
            foreach (var (key, child) in obj) if (key != "$defs") Visit(child);
        }
        Visit(schema);
        foreach (var key in definitions.Select(p => p.Key).Where(k => !used.Contains(k)).ToArray()) definitions.Remove(key);
    }
}
