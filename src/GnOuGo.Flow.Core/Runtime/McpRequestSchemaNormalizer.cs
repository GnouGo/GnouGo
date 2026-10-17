using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Applies JSON-object optionality semantics to resolved MCP requests. A property
/// whose schema does not require it is omitted when its resolved value is JSON
/// null; required properties retain null and fail normal schema validation.
/// </summary>
internal static class McpRequestSchemaNormalizer
{
    public static JsonNode? OmitNullOptionalProperties(JsonNode? request, JsonNode? schema)
    {
        var clone = request?.DeepClone();
        NormalizeNode(clone, schema as JsonObject);
        return clone;
    }

    // Optional producer annotation: JSON Schema number with format=decimal means
    // System.Decimal at the MCP boundary. It adds no workflow arithmetic semantics.
    internal static JsonNode? ConvertDecimals(JsonNode? value, JsonNode? schema, bool output,
        string server, string method, out bool converted)
    {
        converted = false;
        if (schema is not JsonObject root || !HasDecimal(root)) return value;
        var failures = JsonSchemaInstanceValidator.ValidateInstanceFindings(value, root);
        if (failures.Count != 0) throw Failure(failures[0].InstancePointer, "The value does not satisfy its numeric contract.");
        var representations = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = Collect(value, root, "", 0, new(StringComparer.Ordinal));
        if (paths.Count == 0) return value;
        var result = Rewrite(value, "");
        // Inputs must still satisfy the producer's contract after decimal rounding.
        // Outputs were validated in their original representation; ordinary binary64
        // rounding is allowed, and each downstream consumer validates its own input.
        if (!output)
        {
            failures = JsonSchemaInstanceValidator.ValidateInstanceFindings(result, root);
            if (failures.Count != 0) throw Failure(failures[0].InstancePointer, "The converted value does not satisfy its contract.");
        }
        converted = true;
        return result;

        WorkflowRuntimeException Failure(string path, string reason) => new(output ? ErrorCodes.McpCallError : ErrorCodes.InputValidation,
            "MCP decimal conversion failed: " + reason, details: new JsonObject
            { ["server"] = server, ["method"] = method, ["direction"] = output ? "output" : "input", ["instance_pointer"] = path });

        JsonNode? Rewrite(JsonNode? node, string path)
        {
            if (paths.Contains(path))
            {
                if (!JsonSchemaInstanceValidator.TryReadNumber(node, out _) ||
                    !decimal.TryParse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw Failure(path, "Expected a finite number within the decimal range.");
                if (number == 0 && JsonSchemaInstanceValidator.CompareNumbers(node!, JsonValue.Create(0)!) != 0)
                    throw Failure(path, "A nonzero number underflows decimal precision.");
                return output ? JsonValue.Create((double)number) : JsonValue.Create(number);
            }
            if (node is JsonObject obj) return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Rewrite(p.Value, Child(path, p.Key)))));
            if (node is JsonArray arr) return new JsonArray(arr.Select((n, i) => Rewrite(n, Child(path, i.ToString(CultureInfo.InvariantCulture)))).ToArray());
            return node?.DeepClone();
        }

        HashSet<string> Collect(JsonNode? node, JsonObject contract, string path, int depth, HashSet<string> references)
        {
            if (depth > 64) throw Failure(path, "Contract nesting exceeds the supported bound.");
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (contract["$ref"] is { } reference)
            {
                var key = reference.ToString(); var identity = path + "|" + key;
                if (references.Add(identity))
                {
                    if (!JsonSchemaInstanceValidator.TryResolveLocalReference(root, key, out var target) || target is not JsonObject resolved)
                        throw Failure(path, "Decimal conversion requires a resolved local contract.");
                    found.UnionWith(Collect(node, resolved, path, depth + 1, references)); references.Remove(identity);
                }
            }
            if (contract["format"]?.ToString() == "decimal")
            {
                var type = contract["type"];
                if (!(type?.ToString() == "number" || type is JsonArray types && types.Any(t => t?.ToString() == "number") ||
                    type is null && (contract.ContainsKey("$ref") || contract.ContainsKey("allOf") || contract.ContainsKey("anyOf") || contract.ContainsKey("oneOf"))))
                    throw Failure(path, "The decimal annotation requires a number contract.");
                if (node is not null) found.Add(path);
            }
            if (contract["format"]?.ToString() is "decimal" or "double" or "float" && node is not null)
            {
                var format = contract["format"]!.ToString();
                if (representations.TryGetValue(path, out var previous) && previous != format)
                    throw Failure(path, "Applicable contracts declare conflicting numeric representations.");
                representations[path] = format;
            }
            foreach (var keyword in new[] { "not", "contains" })
                if (contract[keyword] is JsonObject unsupported && HasDecimal(unsupported))
                    throw Failure(path, "Decimal conversion cannot be established from " + keyword + ".");
            if (contract["allOf"] is JsonArray all)
                foreach (var part in all.OfType<JsonObject>()) found.UnionWith(Collect(node, part, path, depth + 1, references));
            foreach (var keyword in new[] { "anyOf", "oneOf" })
                if (contract[keyword] is JsonArray branches)
                {
                    HashSet<string>? selected = null;
                    foreach (var branch in branches.OfType<JsonObject>().Where(b => JsonSchemaInstanceValidator.MatchesSchema(node, b, root)))
                    {
                        var next = Collect(node, branch, path, depth + 1, references);
                        next.UnionWith(found);
                        if (selected is not null && !selected.SetEquals(next)) throw Failure(path, "Matching contract branches disagree on decimal conversion.");
                        selected = next;
                    }
                    if (selected is not null) found.UnionWith(selected);
                }
            if (contract["if"] is JsonObject condition && contract[JsonSchemaInstanceValidator.MatchesSchema(node, condition, root) ? "then" : "else"] is JsonObject chosen)
                found.UnionWith(Collect(node, chosen, path, depth + 1, references));
            if (node is JsonObject properties)
            {
                if (contract["dependentSchemas"] is JsonObject dependents)
                    foreach (var (name, dependent) in dependents)
                        if (properties.ContainsKey(name) && dependent is JsonObject dependentContract)
                            found.UnionWith(Collect(node, dependentContract, path, depth + 1, references));
                foreach (var (name, child) in properties)
                {
                    var matched = false;
                    if (contract["properties"]?[name] is JsonObject declared)
                    { found.UnionWith(Collect(child, declared, Child(path, name), depth + 1, references)); matched = true; }
                    if (contract["patternProperties"] is JsonObject patterns)
                        foreach (var (pattern, shape) in patterns)
                            if (shape is JsonObject shapeObject && Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                            { found.UnionWith(Collect(child, shapeObject, Child(path, name), depth + 1, references)); matched = true; }
                    if (!matched && contract["additionalProperties"] is JsonObject additional)
                        found.UnionWith(Collect(child, additional, Child(path, name), depth + 1, references));
                }
            }
            if (node is JsonArray items)
                for (var i = 0; i < items.Count; i++)
                {
                    var itemSchema = contract["prefixItems"] is JsonArray prefix && i < prefix.Count ? prefix[i] : contract["items"];
                    if (itemSchema is JsonObject element) found.UnionWith(Collect(items[i], element, Child(path, i.ToString(CultureInfo.InvariantCulture)), depth + 1, references));
                }
            return found;
        }
    }

    private static string Child(string path, string token) => path + "/" + token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static bool HasDecimal(JsonObject schema)
    {
        if (schema["format"]?.ToString() == "decimal") return true;
        foreach (var keyword in new[] { "$defs", "definitions", "properties", "patternProperties", "dependentSchemas" })
            if (schema[keyword] is JsonObject map && map.Any(p => p.Value is JsonObject item && HasDecimal(item))) return true;
        foreach (var keyword in new[] { "items", "additionalProperties", "contains", "not", "if", "then", "else" })
            if (schema[keyword] is JsonObject child && HasDecimal(child)) return true;
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
            if (schema[keyword] is JsonArray list && list.OfType<JsonObject>().Any(HasDecimal)) return true;
        return false;
    }

    public static bool IsOptionalPropertyPath(JsonObject schema, string validatorField)
    {
        var path = validatorField.StartsWith("input.", StringComparison.Ordinal)
            ? validatorField["input.".Length..]
            : validatorField;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        JsonObject? current = schema;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var arrayIndex = segment.IndexOf('[');
            if (arrayIndex >= 0)
                segment = segment[..arrayIndex];
            if (string.IsNullOrWhiteSpace(segment) || current == null)
                return false;

            var propertySchema = FindPropertySchema(current, segment);
            if (propertySchema == null)
                return false;
            if (index == segments.Length - 1)
                return !IsAlwaysRequired(current, segment);

            var next = propertySchema;
            if (segments[index].Contains('['))
                next = FindItemsSchema(next);
            current = next;
        }

        return false;
    }

    private static void NormalizeNode(JsonNode? value, JsonObject? schema)
    {
        if (value is JsonObject obj && schema != null)
        {
            foreach (var name in obj.Select(static pair => pair.Key).ToArray())
            {
                var propertySchema = FindPropertySchema(schema, name);
                if (propertySchema == null)
                    continue;

                if (obj[name] == null && !IsAlwaysRequired(schema, name))
                {
                    obj.Remove(name);
                    continue;
                }

                NormalizeNode(obj[name], propertySchema);
            }
            return;
        }

        if (value is JsonArray array && schema != null)
        {
            var itemsSchema = FindItemsSchema(schema);
            if (itemsSchema == null)
                return;
            foreach (var item in array)
                NormalizeNode(item, itemsSchema);
        }
    }

    private static JsonObject? FindPropertySchema(JsonObject schema, string propertyName)
    {
        if (schema["properties"] is JsonObject properties
            && properties[propertyName] is JsonObject direct)
        {
            return direct;
        }

        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (schema[keyword] is not JsonArray branches)
                continue;
            foreach (var branch in branches.OfType<JsonObject>())
            {
                var nested = FindPropertySchema(branch, propertyName);
                if (nested != null)
                    return nested;
            }
        }

        return null;
    }

    private static JsonObject? FindItemsSchema(JsonObject schema)
    {
        if (schema["items"] is JsonObject items)
            return items;
        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (schema[keyword] is not JsonArray branches)
                continue;
            foreach (var branch in branches.OfType<JsonObject>())
            {
                var nested = FindItemsSchema(branch);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    private static bool IsAlwaysRequired(JsonObject schema, string propertyName)
    {
        if (schema["required"] is JsonArray required
            && required.Any(item => item is JsonValue scalar
                                    && scalar.TryGetValue<string>(out var value)
                                    && string.Equals(value, propertyName, StringComparison.Ordinal)))
        {
            return true;
        }

        // allOf requirements apply together. oneOf/anyOf requirements are branch
        // dependent and therefore are not unconditional optionality constraints.
        if (schema["allOf"] is JsonArray allOf)
            return allOf.OfType<JsonObject>().Any(branch => IsAlwaysRequired(branch, propertyName));

        return false;
    }
}
