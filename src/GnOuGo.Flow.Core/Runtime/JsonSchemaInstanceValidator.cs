using System.Globalization;
using GnOuGo.Flow.Core.Planning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// AOT-friendly JSON Schema instance validator for Flow runtime contracts.
/// </summary>
internal static class JsonSchemaInstanceValidator
{
    public static IReadOnlyList<string> ValidateInstance(JsonNode? instance, JsonNode? schema)
        => ValidateInstanceFindings(instance, schema).Select(f => f.Message).ToArray();

    internal static IReadOnlyList<PlanningInstanceFinding> ValidateInstanceFindings(JsonNode? instance, JsonNode? schema)
    {
        var errors = new List<PlanningInstanceFinding>();
        if (schema is not JsonObject root)
        {
            errors.Add(new("", "$: schema must be an object", "schema_type"));
            return errors;
        }

        ValidateInstanceNode(instance, root, root, new("$", ""), errors, new HashSet<string>(StringComparer.Ordinal));
        return errors;
    }

    private static void ValidateInstanceNode(
        JsonNode? value,
        JsonObject schema,
        JsonObject root,
        InstanceLocation path,
        List<PlanningInstanceFinding> errors,
        HashSet<string> referenceStack)
    {
        if (TryReadString(schema["$ref"], out var reference))
        {
            if (!TryResolveLocalReference(root, reference, out var referencedSchema) || referencedSchema is not JsonObject referencedObject)
            {
                errors.Add(new(path.Pointer, $"{path}: unresolved schema reference '{reference}'", "$ref"));
                return;
            }

            var referenceKey = $"{reference}|{path.Pointer}";
            if (!referenceStack.Add(referenceKey))
                return;
            ValidateInstanceNode(value, referencedObject, root, path, errors, referenceStack);
            referenceStack.Remove(referenceKey);
        }

        if (schema["allOf"] is JsonArray allOf)
            foreach (var variant in allOf.OfType<JsonObject>())
                ValidateInstanceNode(value, variant, root, path, errors, referenceStack);

        if (schema["anyOf"] is JsonArray anyOf && CountMatchingVariants(value, anyOf, root, path, referenceStack) == 0)
        {
            if (!DescribeSelectedVariant(value, anyOf, root, path, errors, referenceStack))
                errors.Add(new(path.Pointer, $"{path}: value does not match any allowed schema variant", "anyOf"));
            return;
        }

        if (schema["oneOf"] is JsonArray oneOf && CountMatchingVariants(value, oneOf, root, path, referenceStack) != 1)
        {
            errors.Add(new(path.Pointer, $"{path}: value must match exactly one allowed schema variant", "oneOf"));
            return;
        }

        if (schema["if"] is JsonObject condition)
        {
            var conditionErrors = new List<PlanningInstanceFinding>();
            ValidateInstanceNode(value, condition, root, path, conditionErrors, new HashSet<string>(referenceStack, StringComparer.Ordinal));
            var selectedBranch = conditionErrors.Count == 0 ? schema["then"] : schema["else"];
            if (selectedBranch is JsonObject selectedBranchSchema)
                ValidateInstanceNode(value, selectedBranchSchema, root, path, errors, referenceStack);
        }

        if (schema.TryGetPropertyValue("const", out var constant) && !JsonNode.DeepEquals(value, constant))
        {
            errors.Add(new(path.Pointer, $"{path}: value must equal {constant?.ToJsonString() ?? "null"}; received {value?.ToJsonString() ?? "null"}", "const"));
            return;
        }
        if (schema["enum"] is JsonArray allowed && !allowed.Any(candidate => JsonNode.DeepEquals(value, candidate)))
        {
            var allowedText = string.Join(", ", allowed.Select(static candidate => candidate?.ToJsonString() ?? "null"));
            errors.Add(new(path.Pointer, $"{path}: value is not included in enum; received {value?.ToJsonString() ?? "null"}; allowed values: {allowedText}", "enum"));
            return;
        }

        var applicableType = ReadApplicableType(schema, value);
        if (applicableType != null && !MatchesType(value, applicableType))
        {
            errors.Add(new(path.Pointer, $"{path}: expected {applicableType}", "type"));
            return;
        }

        switch (applicableType)
        {
            case "object":
                ValidateObject(value, schema, root, path, errors, referenceStack);
                break;
            case "array":
                ValidateArray(value, schema, root, path, errors, referenceStack);
                break;
            case "string":
                ValidateString(value, schema, path, errors);
                break;
            case "number":
            case "integer":
                ValidateNumber(value, schema, path, errors);
                break;
        }
    }

    private static int CountMatchingVariants(JsonNode? value, JsonArray variants, JsonObject root, InstanceLocation path, HashSet<string> referenceStack)
    {
        var matches = 0;
        foreach (var variant in variants.OfType<JsonObject>())
        {
            var variantErrors = new List<PlanningInstanceFinding>();
            ValidateInstanceNode(value, variant, root, path, variantErrors, new HashSet<string>(referenceStack, StringComparer.Ordinal));
            if (variantErrors.Count == 0)
                matches++;
        }
        return matches;
    }

    // Type and literal tags can identify one intended variant even when a nested
    // field is invalid. Report that field without guessing among ambiguous branches.
    private static bool DescribeSelectedVariant(JsonNode? value, JsonArray variants, JsonObject root, InstanceLocation path,
        List<PlanningInstanceFinding> errors, HashSet<string> referenceStack)
    {
        var candidates = variants.OfType<JsonObject>().Where(v => Possible(v, new(StringComparer.Ordinal))).ToArray();
        if (candidates.Length != 1) return false;
        var details = new List<PlanningInstanceFinding>();
        ValidateInstanceNode(value, candidates[0], root, path, details, new(referenceStack, StringComparer.Ordinal));
        if (details.Count == 0) return false;
        errors.AddRange(details); return true;

        bool Possible(JsonObject variant, HashSet<string> visited)
        {
            if (TryReadString(variant["$ref"], out var reference) && visited.Add(reference) &&
                TryResolveLocalReference(root, reference, out var resolved) && resolved is JsonObject target && !Possible(target, visited)) return false;
            var type = ReadApplicableType(variant, value);
            if (type is not null && !MatchesType(value, type)) return false;
            if (variant.TryGetPropertyValue("const", out var constant) && !JsonNode.DeepEquals(value, constant)) return false;
            if (variant["enum"] is JsonArray allowed && !allowed.Any(v => JsonNode.DeepEquals(v, value))) return false;
            if (value is JsonObject obj && variant["properties"] is JsonObject properties)
                foreach (var (name, property) in properties)
                    if (obj.TryGetPropertyValue(name, out var actual) && property is JsonObject contract &&
                        (contract.TryGetPropertyValue("const", out var tag) && !JsonNode.DeepEquals(actual, tag) ||
                         contract["enum"] is JsonArray tags && !tags.Any(t => JsonNode.DeepEquals(actual, t)))) return false;
            return true;
        }
    }

    private static string? ReadApplicableType(JsonObject schema, JsonNode? value)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var singleType))
            return singleType;
        if (schema["type"] is JsonArray types)
        {
            foreach (var candidate in types.OfType<JsonValue>())
                if (candidate.TryGetValue<string>(out var typeName) && typeName != null && MatchesType(value, typeName))
                    return typeName;
            return types.OfType<JsonValue>().Select(valueNode => valueNode.TryGetValue<string>(out var name) ? name : null).FirstOrDefault();
        }
        if (schema.ContainsKey("properties") || schema.ContainsKey("required") || schema.ContainsKey("additionalProperties")) return "object";
        if (schema.ContainsKey("items") || schema.ContainsKey("minItems") || schema.ContainsKey("maxItems")) return "array";
        if (schema.ContainsKey("pattern") || schema.ContainsKey("minLength") || schema.ContainsKey("maxLength")) return "string";
        if (schema.ContainsKey("minimum") || schema.ContainsKey("maximum") || schema.ContainsKey("multipleOf")) return "number";
        return null;
    }

    private static bool MatchesType(JsonNode? value, string type)
    {
        if (value == null) return type == "null";
        if (type == "object") return value is JsonObject;
        if (type == "array") return value is JsonArray;
        if (value is not JsonValue jsonValue) return false;
        return type switch
        {
            "string" => jsonValue.TryGetValue<string>(out _),
            "boolean" => jsonValue.TryGetValue<bool>(out _),
            "number" => TryReadDecimal(jsonValue, out _),
            "integer" => TryReadDecimal(jsonValue, out var number) && decimal.Truncate(number) == number,
            _ => false
        };
    }

    private static void ValidateObject(JsonNode? value, JsonObject schema, JsonObject root, InstanceLocation path, List<PlanningInstanceFinding> errors, HashSet<string> referenceStack)
    {
        if (value is not JsonObject obj)
            return;
        ValidateCount(obj.Count, schema, "minProperties", "maxProperties", path, "properties", errors);
        if (schema["required"] is JsonArray required)
            foreach (var requiredName in required.OfType<JsonValue>().Select(node => node.TryGetValue<string>(out var name) ? name : null).Where(static name => name != null))
                if (!obj.ContainsKey(requiredName!))
                    errors.Add(new(path.Child(requiredName!).Pointer, $"{path}.{requiredName}: missing required property", "required"));
        if (schema["dependentRequired"] is JsonObject dependentRequired)
        {
            foreach (var (propertyName, dependenciesNode) in dependentRequired)
            {
                if (!obj.ContainsKey(propertyName) || dependenciesNode is not JsonArray dependencies)
                    continue;
                foreach (var dependency in dependencies.OfType<JsonValue>()
                             .Select(node => node.TryGetValue<string>(out var name) ? name : null)
                             .Where(static name => !string.IsNullOrWhiteSpace(name)))
                {
                    if (!obj.ContainsKey(dependency!))
                        errors.Add(new(path.Child(dependency!).Pointer, $"{path}.{dependency}: missing property required by '{propertyName}'", "dependentRequired:" + propertyName));
                }
            }
        }

        var properties = schema["properties"] as JsonObject;
        foreach (var (name, childValue) in obj)
        {
            if (properties != null && properties.TryGetPropertyValue(name, out var childSchema) && childSchema is JsonObject childObject)
            {
                ValidateInstanceNode(childValue, childObject, root, path.Child(name), errors, referenceStack);
                continue;
            }
            if (schema["additionalProperties"] is JsonValue additionalValue
                && additionalValue.TryGetValue<bool>(out var additionalAllowed)
                && !additionalAllowed)
                errors.Add(new(path.Child(name).Pointer, $"{path}.{name}: property is not allowed by schema", "additionalProperties"));
            else if (schema["additionalProperties"] is JsonObject additionalSchema)
                ValidateInstanceNode(childValue, additionalSchema, root, path.Child(name), errors, referenceStack);
        }
    }

    private static void ValidateArray(JsonNode? value, JsonObject schema, JsonObject root, InstanceLocation path, List<PlanningInstanceFinding> errors, HashSet<string> referenceStack)
    {
        if (value is not JsonArray array)
            return;
        ValidateCount(array.Count, schema, "minItems", "maxItems", path, "items", errors);
        if (schema["uniqueItems"] is JsonValue uniqueValue && uniqueValue.TryGetValue<bool>(out var unique) && unique)
            for (var i = 0; i < array.Count; i++)
                for (var j = i + 1; j < array.Count; j++)
                    if (JsonNode.DeepEquals(array[i], array[j]))
                        errors.Add(new(path.Child(j).Pointer, $"{path}: items at indexes {i} and {j} must be unique", "uniqueItems"));
        if (schema["items"] is JsonObject itemSchema)
            for (var i = 0; i < array.Count; i++)
                ValidateInstanceNode(array[i], itemSchema, root, path.Child(i), errors, referenceStack);
    }

    private static void ValidateString(JsonNode? value, JsonObject schema, InstanceLocation path, List<PlanningInstanceFinding> errors)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || text == null)
            return;
        ValidateCount(text.Length, schema, "minLength", "maxLength", path, "characters", errors);
        if (TryReadString(schema["pattern"], out var pattern))
            try
            {
                if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    errors.Add(new(path.Pointer, $"{path}: string does not match pattern '{pattern}'", "pattern"));
            }
            catch (ArgumentException) { errors.Add(new(path.Pointer, $"{path}: schema contains an invalid pattern", "pattern_contract")); }
    }

    private static void ValidateNumber(JsonNode? value, JsonObject schema, InstanceLocation path, List<PlanningInstanceFinding> errors)
    {
        if (!TryReadDecimal(value, out var number))
            return;
        if (TryReadDecimal(schema["minimum"], out var minimum) && number < minimum) errors.Add(new(path.Pointer, $"{path}: number must be >= {minimum}", "minimum"));
        if (TryReadDecimal(schema["maximum"], out var maximum) && number > maximum) errors.Add(new(path.Pointer, $"{path}: number must be <= {maximum}", "maximum"));
        if (TryReadDecimal(schema["exclusiveMinimum"], out var exclusiveMinimum) && number <= exclusiveMinimum) errors.Add(new(path.Pointer, $"{path}: number must be > {exclusiveMinimum}", "exclusiveMinimum"));
        if (TryReadDecimal(schema["exclusiveMaximum"], out var exclusiveMaximum) && number >= exclusiveMaximum) errors.Add(new(path.Pointer, $"{path}: number must be < {exclusiveMaximum}", "exclusiveMaximum"));
        if (TryReadDecimal(schema["multipleOf"], out var multipleOf) && multipleOf > 0 && number % multipleOf != 0) errors.Add(new(path.Pointer, $"{path}: number must be a multiple of {multipleOf}", "multipleOf"));
    }

    private static void ValidateCount(int count, JsonObject schema, string minimumKeyword, string maximumKeyword, InstanceLocation path, string unit, List<PlanningInstanceFinding> errors)
    {
        if (TryReadInteger(schema[minimumKeyword], out var minimum) && count < minimum) errors.Add(new(path.Pointer, $"{path}: must contain at least {minimum} {unit}", minimumKeyword));
        if (TryReadInteger(schema[maximumKeyword], out var maximum) && count > maximum) errors.Add(new(path.Pointer, $"{path}: must contain at most {maximum} {unit}", maximumKeyword));
    }

    private readonly record struct InstanceLocation(string Display, string Pointer)
    {
        public override string ToString() => Display;
        internal InstanceLocation Child(string name) => new(Display + "." + name,
            Pointer + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        internal InstanceLocation Child(int index) => new(Display + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
            Pointer + "/" + index.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryReadString(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var parsed) && parsed != null)
        {
            text = parsed;
            return true;
        }
        text = string.Empty;
        return false;
    }

    private static bool TryReadInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue<long>(out value))
            return true;
        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }
        return false;
    }

    private static bool TryReadDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue<decimal>(out value)) return true;
        if (jsonValue.TryGetValue<long>(out var longValue)) { value = longValue; return true; }
        if (jsonValue.TryGetValue<int>(out var intValue)) { value = intValue; return true; }
        if (jsonValue.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
        {
            try { value = (decimal)doubleValue; return true; }
            catch (OverflowException) { return false; }
        }
        return false;
    }

    private static bool TryResolveLocalReference(JsonObject root, string reference, out JsonNode? resolved)
    {
        resolved = null;
        if (!reference.StartsWith('#')) return false;
        if (reference == "#") { resolved = root; return true; }
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;

        JsonNode? current = root;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current is JsonObject currentObject && currentObject.TryGetPropertyValue(segment, out current))
                continue;
            if (current is JsonArray currentArray && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < currentArray.Count)
            {
                current = currentArray[index];
                continue;
            }
            return false;
        }
        resolved = current;
        return resolved != null;
    }
}
