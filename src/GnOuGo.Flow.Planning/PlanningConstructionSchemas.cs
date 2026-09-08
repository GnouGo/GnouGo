using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning;

/// <summary>Construction schemas expose only fields meaningful for their declared type.</summary>
internal static class PlanningConstructionSchemas
{
    internal const int Version = 29;

    internal static JsonNode[] Variants(JsonNode template) => new[]
        { new[] { "string" }, new[] { "number", "integer", "boolean" }, new[] { "array" }, new[] { "object" } }
        .Select(types =>
        {
            var variant = template.DeepClone();
            var properties = variant["properties"]!.AsObject();
            var fields = Fields(types[0]);
            foreach (var key in properties.Select(p => p.Key).Where(k => !fields.Contains(k)).ToArray()) properties.Remove(key);
            properties["type"]!["enum"] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            variant["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            return variant;
        }).ToArray();

    internal static JsonObject Compact(JsonObject candidate)
    {
        var copy = candidate.DeepClone().AsObject(); Visit(copy); return copy;

        static void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (node is not JsonObject obj) return;
            if (obj["kind"]?.ToString() == "inline")
            {
                var allowed = Fields(obj["type"]?.ToString() ?? "");
                foreach (var name in new[] { "enum", "items", "properties", "additionalProperties" })
                    if (!allowed.Contains(name) && obj.TryGetPropertyValue(name, out var value) && (value is null || value is JsonArray { Count: 0 })) obj.Remove(name);
            }
            foreach (var child in obj.Select(p => p.Value)) Visit(child);
        }
    }

    private static HashSet<string> Fields(string type) => new(new[] { "kind", "type", "nullable", "description" }.Concat(type switch
    {
        "string" => ["enum"], "array" => ["items"], "object" => ["properties", "additionalProperties"], _ => Array.Empty<string>()
    }), StringComparer.Ordinal);
}
