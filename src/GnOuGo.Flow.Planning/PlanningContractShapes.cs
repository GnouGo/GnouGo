using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning;
internal static class PlanningContractShapes
{
    internal static JsonObject Opaque() => new() { ["x-gnougo-opaque"] = true };
    internal static bool IsOpaque(JsonObject schema) => schema["x-gnougo-opaque"]?.ToString() == "true";

    // Import only schema positions, never instance data in const/enum/default.
    // Unknown contents stay opaque while their containing types and constraints survive.
    internal static JsonObject Producer(JsonObject schema)
    {
        var result = schema.DeepClone().AsObject();
        Visit(result, 0);
        return result;

        static void Visit(JsonObject current, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("Producer schema nesting exceeds 32 levels.");
            if (current.Count == 0) { current["x-gnougo-opaque"] = true; return; }
            if (IsOpaque(current)) return;
            bool HasType(string name) => current["type"]?.ToString() == name || current["type"] is JsonArray types && types.Any(t => t?.ToString() == name);
            if (HasType("array") && (current["items"] is null || current["items"]?.ToString() == "true")) current["items"] = Opaque();
            if (HasType("object") && (current["additionalProperties"] is null || current["additionalProperties"]?.ToString() == "true")) current["additionalProperties"] = Opaque();
            foreach (var key in new[] { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas" })
                if (current[key] is JsonObject fields)
                    foreach (var name in fields.Select(p => p.Key).ToArray()) Import(fields, name, depth);
            foreach (var key in new[] { "items", "additionalProperties", "contains", "propertyNames", "not", "if", "then", "else", "unevaluatedProperties", "unevaluatedItems" })
                Import(current, key, depth);
            foreach (var key in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
                if (current[key] is JsonArray alternatives)
                    for (var i = 0; i < alternatives.Count; i++)
                        if (alternatives[i] is JsonObject child) Visit(child, depth + 1);
                        else if (alternatives[i] is JsonValue value && value.TryGetValue<bool>(out var allowed) && allowed) alternatives[i] = Opaque();
        }
        static void Import(JsonObject parent, string name, int depth)
        {
            if (parent[name] is JsonObject child) Visit(child, depth + 1);
            else if (parent[name] is JsonValue value && value.TryGetValue<bool>(out var allowed) && allowed) parent[name] = Opaque();
        }
    }
}
