using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Conservative contract inclusion, never sample-based type inference.</summary>
internal static class PlanningContractCompatibility
{
    internal enum Proof { Compatible, Incompatible, Unknown }
    internal static Proof Analyze(JsonObject actual, JsonObject expected)
    {
        if (Fits(actual, expected)) return Proof.Compatible;
        if (Disjoint(actual, expected, actual, expected, 0, [2048])) return Proof.Incompatible;
        if (Finite(actual) is { } values && ValidatorComplete(actual) && ValidatorComplete(expected) && values.Any(v => PlanningContractValidation.ValidateInstance(v, actual).Count == 0 &&
            PlanningContractValidation.ValidateInstance(v, expected).Count != 0)) return Proof.Incompatible;
        return Proof.Unknown;
    }
    internal static bool Fits(JsonObject actual, JsonObject expected) => Fits(actual, expected, actual, expected, 0, [2048]);

    private static bool Fits(JsonObject actual, JsonObject expected, JsonObject sourceRoot, JsonObject targetRoot, int depth, int[] work)
    {
        if (depth > 32 || --work[0] < 0) return false;
        // This proof implements the runtime's JSON Schema vocabulary. An explicitly
        // declared extension vocabulary needs an implementation, not a model guess.
        if (expected.ContainsKey("$vocabulary") || targetRoot.ContainsKey("$vocabulary")) return false;
        // Inclusion is reflexive even for overlapping oneOf alternatives: the
        // producer already excludes values matching several alternatives. Expanding
        // that source into an inclusive union would lose this useful constraint.
        // Identical reference text is sufficient only in the same schema environment.
        if (JsonNode.DeepEquals(actual, expected) && (!References(actual) || JsonNode.DeepEquals(sourceRoot, targetRoot))) return true;
        if (actual["$ref"] is JsonValue sr)
        {
            var siblings = actual.DeepClone().AsObject(); siblings.Remove("$ref");
            return Resolve(sourceRoot, sr.ToString()) is { } source && Fits(Intersect(source, siblings), expected, sourceRoot, targetRoot, depth + 1, work);
        }
        if (expected["$ref"] is JsonValue tr)
        {
            var siblings = expected.DeepClone().AsObject(); siblings.Remove("$ref");
            return Resolve(targetRoot, tr.ToString()) is { } target && Fits(actual, target, sourceRoot, targetRoot, depth + 1, work) && Fits(actual, siblings, sourceRoot, targetRoot, depth + 1, work);
        }
        if (actual["type"] is JsonArray sourceUnion)
            return sourceUnion.All(type => { var branch = actual.DeepClone().AsObject(); branch["type"] = type!.DeepClone(); return Fits(branch, expected, sourceRoot, targetRoot, depth + 1, work); });
        if (expected["allOf"] is JsonArray targetParts)
        {
            var siblings = expected.DeepClone().AsObject(); siblings.Remove("allOf");
            return Fits(actual, siblings, sourceRoot, targetRoot, depth + 1, work) && targetParts.All(t => Contract(t) is { } part && Fits(actual, part, sourceRoot, targetRoot, depth + 1, work));
        }
        if (actual["allOf"] is JsonArray sourceParts)
        {
            var combined = actual.DeepClone().AsObject(); combined.Remove("allOf");
            foreach (var part in sourceParts)
            {
                if (Contract(part) is not { } contract) return false;
                if (contract["$ref"] is JsonValue reference)
                {
                    if (Resolve(sourceRoot, reference.ToString()) is not { } resolved) return false;
                    var siblings = contract.DeepClone().AsObject(); siblings.Remove("$ref");
                    contract = Intersect(resolved, siblings);
                }
                combined = Intersect(combined, contract);
            }
            return Fits(combined, expected, sourceRoot, targetRoot, depth + 1, work);
        }
        if ((actual["anyOf"] ?? actual["oneOf"]) is JsonArray sources)
        {
            var siblings = actual.DeepClone().AsObject(); siblings.Remove("anyOf"); siblings.Remove("oneOf");
            return sources.Count > 0 && sources.All(s => Contract(s) is { } schema && Fits(Intersect(schema, siblings), expected, sourceRoot, targetRoot, depth + 1, work));
        }
        // Finite domains are checked against the complete target, including oneOf exclusivity.
        if (Finite(actual) is { Count: <= 256 } finite && !References(actual) && ValidatorComplete(expected))
            return finite.Where(v => PlanningContractValidation.ValidateInstance(v, actual).Count == 0)
                .All(v => PlanningContractValidation.ValidateInstance(v, expected).Count == 0);
        if ((expected["anyOf"] ?? expected["oneOf"]) is JsonArray targets)
        {
            var exclusive = expected["anyOf"] is null;
            var siblings = expected.DeepClone().AsObject(); siblings.Remove(exclusive ? "oneOf" : "anyOf");
            if (!Fits(actual, siblings, sourceRoot, targetRoot, depth + 1, work)) return false;
            return targets.Select((target, index) => (target, index)).Any(selected => Contract(selected.target) is { } schema &&
                Fits(actual, schema, sourceRoot, targetRoot, depth + 1, work) && (!exclusive ||
                targets.Where((_, index) => index != selected.index).All(t => Contract(t) is { } other && Disjoint(actual, other, sourceRoot, targetRoot, depth + 1, work))));
        }
        var sourceTypes = Types(actual); var targetTypes = Types(expected);
        if (expected.All(p => IsAnnotation(p.Key))) return true;
        if (sourceTypes.Length == 0 || targetTypes.Length > 0 && !sourceTypes.All(t => targetTypes.Contains(t, StringComparer.Ordinal) || t == "integer" && targetTypes.Contains("number", StringComparer.Ordinal))) return false;
        if (sourceTypes.All(t => t == "null")) return ValidatorComplete(expected) && PlanningContractValidation.ValidateInstance(null, expected).Count == 0;
        if (expected.ContainsKey("const") || expected.ContainsKey("enum")) return false;
        foreach (var (keyword, constraint) in expected)
            if (!Handled.Contains(keyword) && !IsAnnotation(keyword) && Applies(keyword, sourceTypes) && !JsonNode.DeepEquals(actual[keyword], constraint)) return false;
        foreach (var (minimum, maximum) in new[] { ("minLength", "maxLength"), ("minItems", "maxItems"), ("minProperties", "maxProperties") })
        {
            if (!Applies(minimum, sourceTypes)) continue;
            var sourceMinimum = Math.Max(Number(actual[minimum]) ?? 0, minimum == "minProperties"
                ? (actual["required"] as JsonArray ?? []).Select(n => n!.ToString()).Distinct(StringComparer.Ordinal).Count() : 0);
            if (Number(expected[minimum]) is { } min && sourceMinimum < min) return false;
            if (Number(expected[maximum]) is { } max && (Maximum(actual, maximum) is not { } known || known > max)) return false;
        }
        if (sourceTypes.Any(t => t is "integer" or "number") && (!Bound(actual, expected, true) || !Bound(actual, expected, false))) return false;
        if (sourceTypes.Any(t => t is "integer" or "number") && Number(expected["multipleOf"]) is { } multiple && (multiple <= 0 ||
            (Number(actual["multipleOf"]) ?? (sourceTypes.SequenceEqual(["integer"]) ? 1 : (decimal?)null)) is not { } sourceMultiple || sourceMultiple <= 0 || sourceMultiple % multiple != 0)) return false;
        if (sourceTypes.Contains("array", StringComparer.Ordinal) && expected["uniqueItems"]?.ToString() == "true" && actual["uniqueItems"]?.ToString() != "true" && Maximum(actual, "maxItems") is not (0 or 1)) return false;
        if (sourceTypes.Contains("object", StringComparer.Ordinal))
        {
            // Pattern-selected members bypass additionalProperties. Without a proof
            // over those patterns, a closed declared-property check is insufficient.
            if (actual["patternProperties"] is JsonObject { Count: > 0 } &&
                expected.Any(p => p.Key is "properties" or "additionalProperties" or "maxProperties")) return false;
            var produced = actual["properties"] as JsonObject ?? new();
            var wanted = expected["properties"] as JsonObject ?? new();
            var required = (actual["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            if ((expected["required"] as JsonArray ?? []).Any(n => !required.Contains(n!.ToString()))) return false;
            foreach (var (name, schema) in wanted)
                if (produced[name] is { } property && !FitsNode(property, schema, sourceRoot, targetRoot, depth + 1, work)) return false;
            foreach (var (name, schema) in produced.Where(p => !wanted.ContainsKey(p.Key)))
                if (!FitsNode(schema, expected["additionalProperties"], sourceRoot, targetRoot, depth + 1, work)) return false;
            if (actual["additionalProperties"]?.ToString() != "false")
            {
                if (expected["additionalProperties"]?.ToString() == "false") return false;
                if (expected["additionalProperties"] is JsonObject extra && (actual["additionalProperties"] is not JsonObject sourceExtra || !Fits(sourceExtra, extra, sourceRoot, targetRoot, depth + 1, work))) return false;
                foreach (var (name, schema) in wanted.Where(p => !produced.ContainsKey(p.Key)))
                    if (!FitsNode(actual["additionalProperties"], schema, sourceRoot, targetRoot, depth + 1, work)) return false;
            }
        }
        if (sourceTypes.Contains("array", StringComparer.Ordinal) && Number(actual["maxItems"]) != 0)
        {
            var sourcePrefix = actual["prefixItems"] as JsonArray ?? [];
            var targetPrefix = expected["prefixItems"] as JsonArray ?? [];
            for (var index = 0; index < Math.Max(sourcePrefix.Count, targetPrefix.Count); index++)
            {
                if (Number(actual["maxItems"]) is { } maximum && index >= maximum) break;
                var sourceItem = index < sourcePrefix.Count ? sourcePrefix[index] : actual["items"];
                var targetItem = index < targetPrefix.Count ? targetPrefix[index] : expected["items"];
                if (!FitsNode(sourceItem, targetItem, sourceRoot, targetRoot, depth + 1, work)) return false;
            }
            if ((Number(actual["maxItems"]) is not { } max || max > Math.Max(sourcePrefix.Count, targetPrefix.Count)) &&
                !FitsNode(actual["items"], expected["items"], sourceRoot, targetRoot, depth + 1, work)) return false;
        }
        return true;
    }

    private static bool FitsNode(JsonNode? actual, JsonNode? expected, JsonObject sourceRoot, JsonObject targetRoot, int depth, int[] work)
    {
        if (actual is JsonValue { } a && a.TryGetValue<bool>(out var allowed) && !allowed) return true;
        if (expected is null || expected is JsonValue { } e && e.TryGetValue<bool>(out var any) && any) return true;
        if (expected is JsonObject unconstrained && unconstrained.All(p => IsAnnotation(p.Key))) return true;
        return actual is JsonObject source && expected is JsonObject target && Fits(source, target, sourceRoot, targetRoot, depth, work);
    }

    private static bool Disjoint(JsonObject left, JsonObject right, JsonObject leftRoot, JsonObject rightRoot, int depth, int[] work)
    {
        if (depth > 32 || --work[0] < 0) return false;
        if (left["$ref"] is JsonValue lr)
        {
            var siblings = left.DeepClone().AsObject(); siblings.Remove("$ref");
            return Resolve(leftRoot, lr.ToString()) is { } resolved && Disjoint(Intersect(resolved, siblings), right, leftRoot, rightRoot, depth + 1, work);
        }
        if (right["$ref"] is JsonValue) return Disjoint(right, left, rightRoot, leftRoot, depth + 1, work);
        if (Finite(left) is { Count: <= 256 } values && ValidatorComplete(right)) return values.All(v => PlanningContractValidation.ValidateInstance(v, right).Count != 0);
        if (Finite(right) is not null && !References(left)) return Disjoint(right, left, rightRoot, leftRoot, depth + 1, work);
        foreach (var field in new[] { "anyOf", "oneOf" })
        {
            if (left[field] is JsonArray variants) return variants.All(v => Contract(v) is { } branch && Disjoint(branch, right, leftRoot, rightRoot, depth + 1, work));
            if (right[field] is JsonArray) return Disjoint(right, left, rightRoot, leftRoot, depth + 1, work);
        }
        if (left["allOf"] is JsonArray parts && parts.Any(p => Contract(p) is { } part && Disjoint(part, right, leftRoot, rightRoot, depth + 1, work))) return true;
        if (right["allOf"] is JsonArray others && others.Any(p => Contract(p) is { } part && Disjoint(left, part, leftRoot, rightRoot, depth + 1, work))) return true;
        var lt = Types(left); var rt = Types(right);
        if (lt.Length > 0 && rt.Length > 0 && !lt.Any(a => rt.Any(b => a == b || a is "number" or "integer" && b is "number" or "integer"))) return true;
        if (lt.Length == 1 && rt.Length == 1 && lt[0] is "number" or "integer" && rt[0] is "number" or "integer")
            foreach (var (a, b) in new[] { (left, right), (right, left) })
                foreach (var low in new[] { "minimum", "exclusiveMinimum" })
                    foreach (var high in new[] { "maximum", "exclusiveMaximum" })
                        if (Number(a[low]) is { } min && Number(b[high]) is { } max && (min > max || min == max && (low == "exclusiveMinimum" || high == "exclusiveMaximum"))) return true;
        if (lt.SequenceEqual(["object"]) && rt.SequenceEqual(["object"]))
        {
            // A pattern may admit a required member even when additionalProperties
            // is false. Do not use that fallback to prove union exclusivity.
            if (left["patternProperties"] is JsonObject { Count: > 0 } || right["patternProperties"] is JsonObject { Count: > 0 }) return false;
            var required = (left["required"] as JsonArray ?? []).Concat(right["required"] as JsonArray ?? []).Select(n => n!.ToString()).Distinct(StringComparer.Ordinal);
            foreach (var name in required)
            {
                var a = left["properties"]?[name] ?? left["additionalProperties"];
                var b = right["properties"]?[name] ?? right["additionalProperties"];
                if (a?.ToString() == "false" || b?.ToString() == "false" || a is JsonObject ac && b is JsonObject bc && Disjoint(ac, bc, leftRoot, rightRoot, depth + 1, work)) return true;
            }
        }
        return false;
    }

    // An upper bound on a source intersection. Unsupported conjunctions stay conservative;
    // this operation is never used to weaken a destination or an authoritative schema.
    private static JsonObject Intersect(JsonObject left, JsonObject right)
    {
        var result = left.DeepClone().AsObject();
        foreach (var (key, value) in right)
        {
            if (!result.ContainsKey(key) || JsonNode.DeepEquals(result[key], value)) { result[key] = value?.DeepClone(); continue; }
            if (key == "type")
            {
                var types = Types(left).SelectMany(a => Types(right).Select(b => a == b ? a : a is "integer" or "number" && b is "integer" or "number" ? "integer" : null)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (types.Length == 1) result[key] = types[0];
                else if (types.Length > 1) result[key] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                else result["enum"] = new JsonArray();
            }
            else if (key == "required") result[key] = new JsonArray(left[key]!.AsArray().Concat(value!.AsArray()).Select(n => n!.ToString()).Distinct(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            else if (key is "minimum" or "exclusiveMinimum" or "minLength" or "minItems" or "minProperties" && Number(result[key]) is { } low && Number(value) is { } lower) result[key] = Math.Max(low, lower);
            else if (key is "maximum" or "exclusiveMaximum" or "maxLength" or "maxItems" or "maxProperties" && Number(result[key]) is { } high && Number(value) is { } higher) result[key] = Math.Min(high, higher);
            else if (key == "allOf") result[key] = new JsonArray(left[key]!.AsArray().Concat(value!.AsArray()).Select(p => p?.DeepClone()).ToArray());
            else if (key == "enum") result[key] = new JsonArray(left[key]!.AsArray().Where(a => value!.AsArray().Any(b => JsonNode.DeepEquals(a, b))).Select(p => p?.DeepClone()).ToArray());
            // Retaining either source constraint is a sound (possibly weaker) upper bound.
        }
        if (left["properties"] is JsonObject || right["properties"] is JsonObject)
        {
            var lp = left["properties"] as JsonObject ?? new(); var rp = right["properties"] as JsonObject ?? new();
            var properties = new JsonObject();
            foreach (var name in lp.Select(p => p.Key).Concat(rp.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
                properties[name] = And(lp[name] ?? left["additionalProperties"], rp[name] ?? right["additionalProperties"]);
            result["properties"] = properties;
        }
        if (left.ContainsKey("additionalProperties") || right.ContainsKey("additionalProperties")) result["additionalProperties"] = And(left["additionalProperties"], right["additionalProperties"]);
        if (left["items"] is not null && right["items"] is not null && left["prefixItems"] is null && right["prefixItems"] is null) result["items"] = And(left["items"], right["items"]);
        return result;
        static JsonNode And(JsonNode? a, JsonNode? b) => a?.ToString() == "false" || b?.ToString() == "false" ? JsonValue.Create(false)! :
            a is JsonObject ac && b is JsonObject bc ? new JsonObject { ["allOf"] = new JsonArray(ac.DeepClone(), bc.DeepClone()) } :
            a is JsonObject ? a.DeepClone() : b is JsonObject ? b.DeepClone() : JsonValue.Create(true)!;
    }

    // Boolean subschemas are the universal and empty value sets. Normalize only
    // inside the proof; authoritative producer and destination contracts stay intact.
    private static JsonObject? Contract(JsonNode? schema) => schema is JsonObject obj ? obj :
        schema is JsonValue value && value.TryGetValue<bool>(out var allowed) ? allowed ? new() : new() { ["enum"] = new JsonArray() } : null;
    private static IReadOnlyList<JsonNode?>? Finite(JsonObject schema) => schema.TryGetPropertyValue("const", out var constant) ? [constant] : schema["enum"] is JsonArray values ? values.ToArray() : null;
    private static bool References(JsonNode? node) => node is JsonObject obj ? obj.ContainsKey("$ref") || obj.Any(p => References(p.Value)) : node is JsonArray array && array.Any(References);
    private static bool ValidatorComplete(JsonObject schema)
    {
        if (References(schema) || schema.Any(p => !Handled.Contains(p.Key) && !IsAnnotation(p.Key) && p.Key is not ("pattern" or "anyOf" or "oneOf" or "allOf"))) return false;
        foreach (var map in new[] { "properties", "$defs", "definitions" })
            if (schema[map] is JsonObject fields && fields.Any(p => p.Value is JsonObject child && !ValidatorComplete(child))) return false;
        foreach (var keyword in new[] { "items", "additionalProperties" })
            if (schema[keyword] is JsonObject child && !ValidatorComplete(child)) return false;
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
            if (schema[keyword] is JsonArray children && children.OfType<JsonObject>().Any(c => !ValidatorComplete(c))) return false;
        return true;
    }
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    { "$defs", "definitions", "$schema", "$id", "$comment", "description", "title", "default", "examples", "readOnly", "writeOnly", "deprecated", "contentEncoding", "contentMediaType", "contentSchema" };
    // JSON Schema 2020-12 core section 4.3.1: unknown keywords are annotations.
    // Retain them in authoritative contracts; they do not constrain JSON values.
    // Known but unsupported assertions/applicators must still fail closed.
    private static bool IsAnnotation(string keyword) => Annotations.Contains(keyword) ||
        !keyword.StartsWith('$') && !Handled.Contains(keyword) && !Assertions.Contains(keyword);
    private static readonly HashSet<string> Assertions = new(StringComparer.Ordinal)
    {
        "pattern", "format", "anyOf", "oneOf", "allOf", "not", "if", "then", "else",
        "contains", "minContains", "maxContains", "patternProperties", "propertyNames",
        "dependentRequired", "dependentSchemas", "unevaluatedProperties", "unevaluatedItems",
        "dependencies", "additionalItems", "extends", "disallow"
    };
    private static bool Applies(string keyword, string[] types) => keyword switch
    {
        "minLength" or "maxLength" or "pattern" or "format" => types.Contains("string", StringComparer.Ordinal),
        "minItems" or "maxItems" or "uniqueItems" or "contains" or "minContains" or "maxContains" => types.Contains("array", StringComparer.Ordinal),
        "minProperties" or "maxProperties" or "patternProperties" or "propertyNames" or "dependentRequired" or "dependentSchemas" => types.Contains("object", StringComparer.Ordinal),
        _ => true
    };

    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "type", "const", "enum", "$defs", "definitions", "$schema", "$id", "description", "title", "default", "examples",
        "properties", "required", "additionalProperties", "items", "prefixItems", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "multipleOf", "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties", "uniqueItems"
    };
    internal static string[] Types(JsonObject schema) => schema["type"] is JsonArray a ? a.Select(n => n!.ToString()).ToArray() : schema["type"] is JsonValue v ? [v.ToString()] : [];
    private static decimal? Number(JsonNode? value) => value is JsonValue v && decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    private static decimal? Maximum(JsonObject schema, string keyword)
    {
        var explicitBound = Number(schema[keyword]);
        decimal? structural = keyword switch
        {
            "maxItems" when schema["items"]?.ToString() == "false" => (schema["prefixItems"] as JsonArray)?.Count ?? 0,
            "maxProperties" when schema["additionalProperties"]?.ToString() == "false" && schema["patternProperties"] is null => (schema["properties"] as JsonObject)?.Count ?? 0,
            _ => null
        };
        return explicitBound is { } a && structural is { } b ? Math.Min(a, b) : explicitBound ?? structural;
    }
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
