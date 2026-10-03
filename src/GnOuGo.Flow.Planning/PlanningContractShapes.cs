using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning;
internal static class PlanningContractShapes
{
    internal static JsonObject Opaque() => new() { ["x-gnougo-opaque"] = true };
    internal static bool IsOpaque(JsonObject schema) => schema["x-gnougo-opaque"]?.ToString() == "true";

    // Business types need not encode technical value restrictions. A checked
    // identity mapping can validate those without changing data or invoking inference.
    // Never override a finite domain, known constraint, shape or nullability.
    internal static bool CanCheckConstraints(JsonObject actual, JsonObject expected)
    {
        if (PlanningContractCompatibility.Fits(actual, expected)) return false;
        var relaxed = expected.DeepClone().AsObject(); var changed = false;
        Visit(actual, relaxed);
        return changed && PlanningContractCompatibility.Fits(actual, relaxed);
        void Visit(JsonObject source, JsonObject target)
        {
            if (IsOpaque(source) || source.ContainsKey("enum") || source.ContainsKey("const")) return;
            foreach (var family in new[] { new[] { "pattern", "minLength", "maxLength" },
                ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"],
                ["minItems", "maxItems", "uniqueItems"], ["minProperties", "maxProperties"] })
                if (!family.Any(source.ContainsKey)) foreach (var key in family) changed |= target.Remove(key);
            if (source["properties"] is JsonObject fields && target["properties"] is JsonObject wanted)
                foreach (var (name, child) in fields)
                    if (child is JsonObject produced && wanted[name] is JsonObject required) Visit(produced, required);
            if (source["items"] is JsonObject item && target["items"] is JsonObject expectedItem) Visit(item, expectedItem);
        }
    }

    // Only unknown schema leaves may be deferred. All known surrounding contracts
    // must still fit; this cannot turn a known incompatible type into extraction.
    internal static bool CanDefer(JsonObject actual, JsonObject expected)
    {
        if (PlanningContractCompatibility.Fits(actual, expected)) return false;
        var candidate = actual.DeepClone().AsObject(); var unknown = false;
        Visit(candidate);
        return unknown && PlanningContractCompatibility.Fits(candidate, expected, allowUnresolved: true);
        void Visit(JsonObject schema)
        {
            if (IsOpaque(schema)) { schema.Clear(); unknown = true; return; }
            foreach (var name in new[] { "properties", "patternProperties", "$defs", "definitions" })
                if (schema[name] is JsonObject fields) foreach (var field in fields.Select(p => p.Value).OfType<JsonObject>()) Visit(field);
            foreach (var name in new[] { "items", "additionalProperties", "contains" }) if (schema[name] is JsonObject child) Visit(child);
            foreach (var name in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
                if (schema[name] is JsonArray children) foreach (var child in children.OfType<JsonObject>()) Visit(child);
        }
    }

    // An empty alternative contributes no possible element. Keep the complete
    // collection contract at the runtime guard; derive only the iteration's item type.
    internal static JsonObject? IterationItems(JsonObject schema)
    {
        var items = new List<JsonObject>();
        if (!Collect(schema, 0)) return null;
        var distinct = items.DistinctBy(s => s.ToJsonString()).ToArray();
        return distinct.Length switch
        {
            0 => Opaque(),
            1 => distinct[0].DeepClone().AsObject(),
            _ => new() { ["anyOf"] = new JsonArray(distinct.Select(s => (JsonNode)s.DeepClone()).ToArray()) }
        };
        bool Collect(JsonObject current, int depth)
        {
            if (depth > 32) return false;
            if ((current["anyOf"] ?? current["oneOf"]) is JsonArray alternatives)
                return alternatives.Count > 0 && alternatives.All(a => a is JsonObject variant && Collect(variant, depth + 1));
            if (current["type"]?.ToString() != "array") return false;
            if (current["const"] is JsonArray { Count: 0 }) return true;
            // Unspecified contents can pass through but cannot establish typed fields.
            items.Add(current["items"] is JsonObject item ? item : Opaque());
            return true;
        }
    }

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
