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
            foreach (var child in obj.Select(p => p.Value)) Visit(child);
            if (obj["kind"]?.ToString() == "inline")
            {
                // Legacy construction allowed object field declarations on arrays.
                // There is one lossless placement when an inline object item already
                // exists and all duplicate declarations agree. Conflicts remain invalid.
                if (obj["type"]?.ToString() == "array" && obj["properties"] is JsonArray { Count: > 0 } misplaced &&
                    obj["items"] is JsonObject item && item["kind"]?.ToString() == "inline" && item["type"]?.ToString() == "object" &&
                    item["properties"] is JsonArray fields && Ports(misplaced) is { } source && Ports(fields) is { } target &&
                    source.All(p => !target.TryGetValue(p.Key, out var existing) || JsonNode.DeepEquals(existing, p.Value)))
                {
                    foreach (var field in source.Where(p => !target.ContainsKey(p.Key))) fields.Add(field.Value.DeepClone());
                    obj.Remove("properties");
                }
                var allowed = Fields(obj["type"]?.ToString() ?? "");
                foreach (var name in new[] { "enum", "items", "properties", "additionalProperties" })
                    if (!allowed.Contains(name) && obj.TryGetPropertyValue(name, out var value) && (value is null || value is JsonArray { Count: 0 })) obj.Remove(name);
            }
        }

        static Dictionary<string, JsonObject>? Ports(JsonArray ports)
        {
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var port in ports)
                if (port is not JsonObject field || field["name"] is not JsonValue name || !name.TryGetValue<string>(out var text) ||
                    string.IsNullOrWhiteSpace(text) || !result.TryAdd(text, field)) return null;
            return result;
        }
    }

    private static HashSet<string> Fields(string type) => new(new[] { "kind", "type", "nullable", "description" }.Concat(type switch
    {
        "string" => ["enum"], "array" => ["items"], "object" => ["properties", "additionalProperties"], _ => Array.Empty<string>()
    }), StringComparer.Ordinal);
}
