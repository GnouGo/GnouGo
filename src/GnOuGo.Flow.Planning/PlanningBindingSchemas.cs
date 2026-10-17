using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

// Translates authoritative literal shapes into the existing TaskValue envelope.
// References, member completeness/uniqueness and cross-field constraints remain semantic checks.
internal static class PlanningBindingSchemas
{
    internal static JsonObject For(JsonObject schema, JsonObject definitions, bool literalOnly = false, bool workspace = false, int depth = 0)
    {
        var fallback = PlanningSchemas.Ref(literalOnly ? "literal" : "value");
        if (depth > 16 || schema.ContainsKey("$ref")) return fallback;
        if (schema["anyOf"] is JsonArray union)
            return new() { ["anyOf"] = new JsonArray(union.OfType<JsonObject>().Select(s => (JsonNode)For(s, definitions, literalOnly, workspace, depth + 1)).ToArray()) };
        var declared = TaskOperations.FiniteDomain(schema);
        var domain = declared.ContainsKey("const") ? new JsonArray(declared["const"]?.DeepClone()) : declared["enum"] as JsonArray;
        var types = schema["type"] is JsonArray list ? list.Select(t => t!.ToString()).ToArray() : schema["type"] is JsonValue value ? [value.ToString()] :
            domain is not null ? domain.Select(n => n is null ? "null" : n.GetValueKind() switch
            { System.Text.Json.JsonValueKind.String => "string", System.Text.Json.JsonValueKind.Number => "number", System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean", System.Text.Json.JsonValueKind.Object => "object", _ => "array" }).Distinct().ToArray() : [];
        if (types.Length == 0) return fallback;
        var alternatives = new JsonArray();
        foreach (var type in types.Distinct(StringComparer.Ordinal))
        {
            if (type == "null") { alternatives.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum("null")))); continue; }
            if (type is "string" or "integer" or "number" or "boolean")
            {
                var scalar = PlanningSchemas.Type(type);
                foreach (var keyword in new[] { "enum", "const", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "pattern" })
                {
                    var constraint = declared[keyword] ?? schema[keyword];
                    if (constraint is not null) scalar[keyword] = constraint.DeepClone();
                }
                if (domain is not null)
                {
                    scalar.Remove("const");
                    var members = domain.Where(n => n is not null && PlanningContractValidation.ValidateInstance(n, PlanningSchemas.Type(type)).Count == 0).Select(n => n!.DeepClone()).ToArray();
                    if (members.Length == 0) continue;
                    scalar["enum"] = new JsonArray(members);
                }
                var kind = type == "integer" ? "number" : type;
                alternatives.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum(kind)), (kind == "string" ? "text" : kind, scalar)));
            }
            else if (type == "array")
            {
                var items = PlanningSchemas.Array(schema["items"] is JsonObject element ? For(element, definitions, literalOnly, false, depth + 1) : fallback.DeepClone().AsObject());
                foreach (var keyword in new[] { "minItems", "maxItems" }) if (schema[keyword] is { } constraint) items[keyword] = constraint.DeepClone();
                alternatives.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum("array")), ("items", items)));
            }
            else if (type == "object")
            {
                var members = new JsonArray(); var properties = schema["properties"] as JsonObject ?? new();
                foreach (var (name, contract) in properties)
                    members.Add((JsonNode)PlanningSchemas.Object(("name", PlanningSchemas.Enum(name)), ("value", contract is JsonObject port ? For(port, definitions, literalOnly, false, depth + 1) : fallback.DeepClone().AsObject())));
                var closed = schema["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var allowed) && !allowed;
                if (!closed) members.Add((JsonNode)PlanningSchemas.Object(("name", PlanningSchemas.String()), ("value", schema["additionalProperties"] is JsonObject additional ? For(additional, definitions, literalOnly, false, depth + 1) : fallback.DeepClone().AsObject())));
                var array = members.Count == 0 ? PlanningSchemas.Array(fallback.DeepClone().AsObject(), 0, 0) :
                    PlanningSchemas.Array(members.Count == 1 ? members[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = members });
                alternatives.Add((JsonNode)PlanningSchemas.Object(("kind", PlanningSchemas.Enum("object")), ("members", array)));
            }
        }
        if (!literalOnly)
        {
            // Literal shape constraints must not rule out correctly typed business references.
            var values = definitions["value"]!["anyOf"]!.AsArray();
            var candidates = values.Concat(definitions["binding"]?["anyOf"]?.AsArray() ?? []);
            foreach (var binding in candidates.OfType<JsonObject>())
            {
                var kinds = binding["properties"]?["kind"]?["enum"]?.AsArray().Select(k => k!.ToString()).ToArray() ?? [];
                if (kinds.Length == 0 || kinds.Any(k => k is "null" or "string" or "number" or "boolean" or "array" or "object")) continue;
                if (workspace && kinds.Any(k => k is not ("output" or "field"))) continue;
                if (kinds.Contains("arithmetic") && !types.Any(t => t is "number" or "integer")) continue;
                if (kinds.Contains("lookup") && !types.Contains("array")) continue;
                if (kinds.Contains("flatten"))
                {
                    var allowedKinds = kinds.Where(k => k == "flatten" ? types.Contains("array") : k != "json" || types.Contains("string")).ToArray();
                    if (allowedKinds.Length == 0) continue;
                    var narrowed = binding.DeepClone();
                    narrowed["properties"]!["kind"] = PlanningSchemas.Enum(allowedKinds);
                    alternatives.Add(narrowed); continue;
                }
                if (kinds.Contains("json") && !types.Contains("string")) continue;
                alternatives.Add(binding.DeepClone());
            }
        }
        var result = alternatives.Count == 0 ? fallback : alternatives.Count == 1 ? alternatives[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = alternatives };
        if (types.Any(t => t is "object" or "array"))
        {
            // Bound schema nesting without erasing nested contract constraints.
            var name = "bindingContract" + definitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            definitions[name] = result; return PlanningSchemas.Ref(name);
        }
        return result;
    }
}
