using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Conservative contract inclusion, never sample-based type inference.</summary>
internal static class PlanningContractCompatibility
{
    internal static bool Fits(JsonObject actual, JsonObject expected) => Fits(actual, expected, actual, expected, 0);

    private static bool Fits(JsonObject actual, JsonObject expected, JsonObject sourceRoot, JsonObject targetRoot, int depth)
    {
        if (depth > 32) return false;
        if (actual["$ref"] is JsonValue sr) return Resolve(sourceRoot, sr.ToString()) is { } source && Fits(source, expected, sourceRoot, targetRoot, depth + 1);
        if (expected["$ref"] is JsonValue tr)
        {
            var siblings = expected.DeepClone().AsObject(); siblings.Remove("$ref");
            return Resolve(targetRoot, tr.ToString()) is { } target && Fits(actual, target, sourceRoot, targetRoot, depth + 1) && Fits(actual, siblings, sourceRoot, targetRoot, depth + 1);
        }
        if (actual["type"] is JsonArray sourceUnion)
            return sourceUnion.All(type => { var branch = actual.DeepClone().AsObject(); branch["type"] = type!.DeepClone(); return Fits(branch, expected, sourceRoot, targetRoot, depth + 1); });
        if ((actual["anyOf"] ?? actual["oneOf"]) is JsonArray sources)
            return sources.Count > 0 && sources.All(s => s is JsonObject schema && Fits(schema, expected, sourceRoot, targetRoot, depth + 1));
        if ((expected["anyOf"] ?? expected["oneOf"]) is JsonArray targets)
        {
            var siblings = expected.DeepClone().AsObject(); siblings.Remove("anyOf"); siblings.Remove("oneOf");
            if (!Fits(actual, siblings, sourceRoot, targetRoot, depth + 1)) return false;
            if (!targets.Any(t => t is JsonObject schema && Fits(actual, schema, sourceRoot, targetRoot, depth + 1))) return false;
            // For oneOf, other alternatives must be provably disjoint; overlap is not proof.
            return expected["oneOf"] is null || targets.OfType<JsonObject>().Count(t => Types(t).Length == 0 || Types(t).Intersect(Types(actual), StringComparer.Ordinal).Any() ||
                Types(t).Any(k => k is "number" or "integer") && Types(actual).Any(k => k is "number" or "integer")) == 1;
        }
        if (actual.TryGetPropertyValue("const", out var constant)) return PlanningContractValidation.ValidateInstance(constant, expected).Count == 0;
        if (actual["enum"] is JsonArray values && values.Count > 0)
            return values.All(v => PlanningContractValidation.ValidateInstance(v, expected).Count == 0);
        var sourceTypes = Types(actual); var targetTypes = Types(expected);
        if (sourceTypes.Length == 0 || targetTypes.Length > 0 && !sourceTypes.All(t => targetTypes.Contains(t, StringComparer.Ordinal) || t == "integer" && targetTypes.Contains("number", StringComparer.Ordinal))) return false;
        if (sourceTypes.All(t => t == "null")) return PlanningContractValidation.ValidateInstance(null, expected).Count == 0;
        if (expected.ContainsKey("const") || expected.ContainsKey("enum")) return false;
        foreach (var (keyword, constraint) in expected)
            if (!Handled.Contains(keyword) && !JsonNode.DeepEquals(actual[keyword], constraint)) return false;
        foreach (var (minimum, maximum) in new[] { ("minLength", "maxLength"), ("minItems", "maxItems"), ("minProperties", "maxProperties") })
        {
            if (Number(expected[minimum]) is { } min && (Number(actual[minimum]) ?? 0) < min) return false;
            if (Number(expected[maximum]) is { } max && (Number(actual[maximum]) is not { } known || known > max)) return false;
        }
        if (!Bound(actual, expected, true) || !Bound(actual, expected, false)) return false;
        if (Number(expected["multipleOf"]) is { } multiple && (multiple <= 0 || Number(actual["multipleOf"]) is not { } sourceMultiple || sourceMultiple <= 0 || sourceMultiple % multiple != 0)) return false;
        if (expected["uniqueItems"]?.ToString() == "true" && actual["uniqueItems"]?.ToString() != "true" && Number(actual["maxItems"]) is not (0 or 1)) return false;
        if (sourceTypes.Contains("object", StringComparer.Ordinal))
        {
            var produced = actual["properties"] as JsonObject ?? new();
            var wanted = expected["properties"] as JsonObject ?? new();
            var required = (actual["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            if ((expected["required"] as JsonArray ?? []).Any(n => !required.Contains(n!.ToString()))) return false;
            foreach (var (name, schema) in wanted)
                if (produced[name] is JsonObject property && schema is JsonObject contract && !Fits(property, contract, sourceRoot, targetRoot, depth + 1)) return false;
            foreach (var (name, schema) in produced.Where(p => !wanted.ContainsKey(p.Key)))
                if (expected["additionalProperties"]?.ToString() == "false" || expected["additionalProperties"] is JsonObject extra && (schema is not JsonObject field || !Fits(field, extra, sourceRoot, targetRoot, depth + 1))) return false;
            if (actual["additionalProperties"]?.ToString() != "false")
            {
                if (expected["additionalProperties"]?.ToString() == "false") return false;
                if (expected["additionalProperties"] is JsonObject extra && (actual["additionalProperties"] is not JsonObject sourceExtra || !Fits(sourceExtra, extra, sourceRoot, targetRoot, depth + 1))) return false;
                foreach (var (name, schema) in wanted.Where(p => !produced.ContainsKey(p.Key)))
                    if (schema is JsonObject contract && (actual["additionalProperties"] is not JsonObject field || !Fits(field, contract, sourceRoot, targetRoot, depth + 1))) return false;
            }
        }
        if (sourceTypes.Contains("array", StringComparer.Ordinal) && Number(actual["maxItems"]) != 0 && expected["items"] is JsonObject items &&
            (actual["items"] is not JsonObject producedItems || !Fits(producedItems, items, sourceRoot, targetRoot, depth + 1))) return false;
        return true;
    }

    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "type", "const", "enum", "$defs", "definitions", "$schema", "$id", "description", "title", "default", "examples",
        "properties", "required", "additionalProperties", "items", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "multipleOf", "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties", "uniqueItems"
    };
    internal static string[] Types(JsonObject schema) => schema["type"] is JsonArray a ? a.Select(n => n!.ToString()).ToArray() : schema["type"] is JsonValue v ? [v.ToString()] : [];
    private static decimal? Number(JsonNode? value) => value is JsonValue v && decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    private static JsonObject? Resolve(JsonObject root, string reference) => reference.StartsWith("#/", StringComparison.Ordinal) ? PlanningFieldPaths.ReadOptional(root, reference[1..]) as JsonObject : null;
    private static bool Bound(JsonObject actual, JsonObject expected, bool lower)
    {
        var inclusive = lower ? "minimum" : "maximum"; var exclusive = lower ? "exclusiveMinimum" : "exclusiveMaximum";
        foreach (var keyword in new[] { inclusive, exclusive })
        {
            if (Number(expected[keyword]) is not { } target) continue;
            var satisfied = false;
            foreach (var sourceKeyword in new[] { inclusive, exclusive })
                if (Number(actual[sourceKeyword]) is { } source && ((lower ? source > target : source < target) || source == target && (keyword == inclusive || sourceKeyword == exclusive))) satisfied = true;
            if (!satisfied) return false;
        }
        return true;
    }
}
