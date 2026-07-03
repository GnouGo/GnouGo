using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed record StructuredOutputContract(
    JsonNode? Schema,
    bool? Strict,
    IReadOnlyList<string> Errors,
    bool IsDynamic = false);

/// <summary>
/// Shared JSON Schema checks for LLM structured output and runtime MCP arguments.
/// The supported instance-validation subset intentionally matches the schema keywords
/// accepted by GnOuGo's workflow contracts.
/// </summary>
internal static class JsonSchemaContractValidator
{
    private static readonly HashSet<string> JsonTypes = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "number", "integer", "boolean", "null"
    };

    private static readonly HashSet<string> StructuredOutputFields = new(StringComparer.Ordinal)
    {
        "schema_inline", "schema_ref", "strict"
    };

    private static readonly string[] UnsupportedStrictKeywords =
    {
        "allOf", "oneOf", "uniqueItems", "minProperties", "maxProperties"
    };

    private static readonly string[] UnsupportedRuntimeKeywords =
    {
        "not", "dependentRequired", "dependentSchemas", "if", "then", "else",
        "patternProperties", "contains", "minContains", "maxContains", "prefixItems",
        "propertyNames", "unevaluatedProperties", "unevaluatedItems"
    };

    private static readonly HashSet<string> SupportedStrictFormats = new(StringComparer.Ordinal)
    {
        "date-time", "time", "date", "duration", "email", "hostname", "ipv4", "ipv6", "uuid"
    };

    internal static StructuredOutputContract ValidateStructuredOutput(
        JsonNode? structuredOutputNode,
        bool allowDynamicSchemaReference)
    {
        var errors = new List<string>();
        if (structuredOutputNode is not JsonObject structuredOutput)
        {
            return new StructuredOutputContract(
                null,
                null,
                new[] { "structured_output must be an object" });
        }

        foreach (var field in structuredOutput.Select(static property => property.Key))
        {
            if (!StructuredOutputFields.Contains(field))
                errors.Add($"structured_output.{field}: unknown field; allowed fields are schema_inline, schema_ref, strict");
        }

        var hasInline = structuredOutput.TryGetPropertyValue("schema_inline", out var inline) && inline != null;
        var hasReference = structuredOutput.TryGetPropertyValue("schema_ref", out var reference) && reference != null;
        if (hasInline == hasReference)
        {
            errors.Add(hasInline
                ? "structured_output: schema_inline and schema_ref are mutually exclusive"
                : "structured_output: exactly one of schema_inline or schema_ref is required");
        }

        bool? strict = null;
        if (structuredOutput.TryGetPropertyValue("strict", out var strictNode) && strictNode != null)
        {
            if (strictNode is JsonValue strictValue && strictValue.TryGetValue<bool>(out var parsedStrict))
                strict = parsedStrict;
            else
                errors.Add("structured_output.strict: expected boolean");
        }

        var schema = hasInline ? inline : hasReference ? reference : null;
        if (schema is JsonValue value
            && value.TryGetValue<string>(out var expression)
            && expression?.Contains("${", StringComparison.Ordinal) == true)
        {
            if (allowDynamicSchemaReference && hasReference)
                return new StructuredOutputContract(null, strict, errors, IsDynamic: true);

            errors.Add("structured_output.schema_ref: expression did not resolve to a JSON Schema object");
            return new StructuredOutputContract(null, strict, errors);
        }

        if (schema != null)
        {
            var normalized = NormalizeSchema(schema.DeepClone());
            errors.AddRange(ValidateSchema(normalized, strict == true));
            schema = normalized;
        }

        return new StructuredOutputContract(schema, strict, errors);
    }

    internal static IReadOnlyList<string> ValidateSchema(JsonNode? schema, bool strictProfile)
    {
        var errors = new List<string>();
        if (schema is not JsonObject root)
        {
            errors.Add("structured_output schema must be a JSON Schema object");
            return errors;
        }

        var statistics = new StrictSchemaStatistics();
        ValidateSchemaNode(root, root, "$", isRoot: true, strictProfile, errors, statistics, depth: 1);

        if (strictProfile)
        {
            if (statistics.PropertyCount > 5000)
                errors.Add($"$: strict structured output supports at most 5000 object properties, found {statistics.PropertyCount}");
            if (statistics.MaximumDepth > 10)
                errors.Add($"$: strict structured output supports at most 10 levels of nesting, found {statistics.MaximumDepth}");
            if (statistics.EnumValueCount > 1000)
                errors.Add($"$: strict structured output supports at most 1000 enum values, found {statistics.EnumValueCount}");
            if (statistics.TotalNameAndEnumLength > 120000)
                errors.Add("$: strict structured output property/definition/enum/const text exceeds 120000 characters");
        }

        return errors;
    }

    internal static IReadOnlyList<string> ValidateInstance(JsonNode? instance, JsonNode? schema)
    {
        var errors = new List<string>();
        if (schema is not JsonObject root)
        {
            errors.Add("$: schema must be an object");
            return errors;
        }

        ValidateInstanceNode(instance, root, root, "$", errors, new HashSet<string>(StringComparer.Ordinal));
        return errors;
    }

    private static JsonNode NormalizeSchema(JsonNode schema)
    {
        if (schema is not JsonObject obj)
            return schema;

        if (obj.ContainsKey("type"))
        {
            if (obj["type"] == null)
                obj["type"] = "null";
            else if (obj["type"] is JsonArray types)
            {
                for (var i = 0; i < types.Count; i++)
                    if (types[i] == null)
                        types[i] = "null";
            }
        }

        NormalizeBoolean(obj, "additionalProperties");
        NormalizeBoolean(obj, "uniqueItems");
        foreach (var keyword in new[] { "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties" })
            NormalizeInteger(obj, keyword);
        foreach (var keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf" })
            NormalizeNumber(obj, keyword);

        if (obj["properties"] is JsonObject properties)
            foreach (var child in properties.Select(static property => property.Value).OfType<JsonObject>())
                NormalizeSchema(child);
        if (obj["items"] is JsonObject items)
            NormalizeSchema(items);
        if (obj["additionalProperties"] is JsonObject additionalProperties)
            NormalizeSchema(additionalProperties);
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
            if (obj[keyword] is JsonArray variants)
                foreach (var child in variants.OfType<JsonObject>())
                    NormalizeSchema(child);
        foreach (var definitionsKeyword in new[] { "$defs", "definitions" })
            if (obj[definitionsKeyword] is JsonObject definitions)
                foreach (var child in definitions.Select(static property => property.Value).OfType<JsonObject>())
                    NormalizeSchema(child);

        return obj;
    }

    private static void NormalizeBoolean(JsonObject obj, string keyword)
    {
        if (TryReadString(obj[keyword], out var text) && bool.TryParse(text, out var value))
            obj[keyword] = value;
    }

    private static void NormalizeInteger(JsonObject obj, string keyword)
    {
        if (TryReadString(obj[keyword], out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            obj[keyword] = value is >= int.MinValue and <= int.MaxValue
                ? JsonValue.Create((int)value)
                : JsonValue.Create(value);
        }
    }

    private static void NormalizeNumber(JsonObject obj, string keyword)
    {
        if (TryReadString(obj[keyword], out var text)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            if (decimal.Truncate(value) == value)
            {
                if (value is >= int.MinValue and <= int.MaxValue)
                {
                    obj[keyword] = JsonValue.Create((int)value);
                    return;
                }

                if (value is >= long.MinValue and <= long.MaxValue)
                {
                    obj[keyword] = JsonValue.Create((long)value);
                    return;
                }
            }

            obj[keyword] = JsonValue.Create(value);
        }
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

    private static void ValidateSchemaNode(
        JsonNode? schema,
        JsonObject root,
        string path,
        bool isRoot,
        bool strictProfile,
        List<string> errors,
        StrictSchemaStatistics statistics,
        int depth)
    {
        statistics.MaximumDepth = Math.Max(statistics.MaximumDepth, depth);
        if (schema is not JsonObject obj)
        {
            errors.Add($"{path}: schema must be an object");
            return;
        }

        if (obj.TryGetPropertyValue("$ref", out var referenceNode))
        {
            if (!TryReadString(referenceNode, out var reference) || string.IsNullOrWhiteSpace(reference))
                errors.Add($"{path}.$ref: expected a non-empty string");
            else if (!TryResolveLocalReference(root, reference, out _))
                errors.Add($"{path}.$ref: unresolved or unsupported reference '{reference}'; only local '#' references are supported");
        }

        var declaredTypes = ReadDeclaredTypes(obj, path, errors);
        if (isRoot && strictProfile)
        {
            if (obj.ContainsKey("anyOf"))
                errors.Add("$: strict structured output root must not use anyOf");
            if (declaredTypes.Count != 1 || !declaredTypes.Contains("object"))
                errors.Add("$: strict structured output root must declare type: object");
        }

        if (obj["properties"] is JsonObject properties)
        {
            statistics.PropertyCount += properties.Count;
            foreach (var (name, child) in properties)
            {
                statistics.TotalNameAndEnumLength += name.Length;
                ValidateSchemaNode(child, root, $"{path}.properties.{name}", false, strictProfile, errors, statistics, depth + 1);
            }
        }
        else if (obj.ContainsKey("properties"))
        {
            errors.Add($"{path}.properties: expected object");
        }

        ValidateRequired(obj, path, strictProfile, errors);

        if (obj.TryGetPropertyValue("additionalProperties", out var additionalProperties)
            && (additionalProperties == null
                || (!IsBoolean(additionalProperties) && additionalProperties is not JsonObject)))
        {
            errors.Add($"{path}.additionalProperties: expected boolean or schema object");
        }
        if (additionalProperties is JsonObject additionalSchema)
            ValidateSchemaNode(additionalSchema, root, $"{path}.additionalProperties", false, strictProfile, errors, statistics, depth + 1);

        if (strictProfile && IsObjectSchema(obj))
        {
            if (!DeclaresType(obj, "object"))
                errors.Add($"{path}.type: strict object schemas must explicitly declare type: object");
            if (obj["properties"] is not JsonObject)
                errors.Add($"{path}.properties: strict object schemas require an object");
            if (obj["required"] is not JsonArray)
                errors.Add($"{path}.required: strict object schemas require an array listing every property");
            if (obj["additionalProperties"] is not JsonValue additionalValue
                || !additionalValue.TryGetValue<bool>(out var allowed)
                || allowed)
            {
                errors.Add($"{path}.additionalProperties: strict object schemas require false");
            }
        }

        if (obj.TryGetPropertyValue("items", out var items))
            ValidateSchemaNode(items, root, $"{path}.items", false, strictProfile, errors, statistics, depth + 1);
        else if (strictProfile && DeclaresType(obj, "array"))
            errors.Add($"{path}.items: strict array schemas require an item schema");

        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (!obj.TryGetPropertyValue(keyword, out var variantsNode))
                continue;
            if (variantsNode is not JsonArray variants || variants.Count == 0)
            {
                errors.Add($"{path}.{keyword}: expected non-empty schema array");
                continue;
            }
            for (var i = 0; i < variants.Count; i++)
                ValidateSchemaNode(variants[i], root, $"{path}.{keyword}[{i}]", false, strictProfile, errors, statistics, depth + 1);
        }

        if (obj["enum"] is JsonArray enumValues)
        {
            if (enumValues.Count == 0)
                errors.Add($"{path}.enum: expected at least one value");
            statistics.EnumValueCount += enumValues.Count;
            var seen = new List<JsonNode?>();
            foreach (var enumValue in enumValues)
            {
                if (seen.Any(existing => JsonNode.DeepEquals(existing, enumValue)))
                    errors.Add($"{path}.enum: duplicate values are not allowed");
                seen.Add(enumValue);
                if (enumValue is JsonValue stringValue && stringValue.TryGetValue<string>(out var enumText) && enumText != null)
                    statistics.TotalNameAndEnumLength += enumText.Length;
            }
            if (strictProfile && enumValues.Count > 250)
            {
                var enumStringLength = enumValues.OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var enumText) ? enumText?.Length ?? 0 : 0)
                    .Sum();
                if (enumStringLength > 15000)
                    errors.Add($"{path}.enum: more than 250 values may contain at most 15000 string characters");
            }
        }
        else if (obj.ContainsKey("enum"))
        {
            errors.Add($"{path}.enum: expected array");
        }

        if (obj["const"] is JsonValue constValue && constValue.TryGetValue<string>(out var constText) && constText != null)
            statistics.TotalNameAndEnumLength += constText.Length;

        ValidateNonNegativeIntegerKeyword(obj, path, "minLength", errors);
        ValidateNonNegativeIntegerKeyword(obj, path, "maxLength", errors);
        ValidateNonNegativeIntegerKeyword(obj, path, "minItems", errors);
        ValidateNonNegativeIntegerKeyword(obj, path, "maxItems", errors);
        ValidateNonNegativeIntegerKeyword(obj, path, "minProperties", errors);
        ValidateNonNegativeIntegerKeyword(obj, path, "maxProperties", errors);
        ValidateRange(obj, path, "minLength", "maxLength", errors);
        ValidateRange(obj, path, "minItems", "maxItems", errors);
        ValidateRange(obj, path, "minProperties", "maxProperties", errors);

        foreach (var keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf" })
            if (obj.ContainsKey(keyword) && !TryReadDecimal(obj[keyword], out _))
                errors.Add($"{path}.{keyword}: expected number");
        if (TryReadDecimal(obj["multipleOf"], out var multipleOf) && multipleOf <= 0)
            errors.Add($"{path}.multipleOf: expected a number greater than zero");
        if (TryReadDecimal(obj["minimum"], out var minimum)
            && TryReadDecimal(obj["maximum"], out var maximum)
            && minimum > maximum)
            errors.Add($"{path}: minimum must be less than or equal to maximum");

        if (obj["pattern"] is JsonValue patternValue && patternValue.TryGetValue<string>(out var pattern) && pattern != null)
        {
            try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { errors.Add($"{path}.pattern: invalid regular expression"); }
        }
        else if (obj.ContainsKey("pattern"))
        {
            errors.Add($"{path}.pattern: expected string");
        }

        if (obj.TryGetPropertyValue("format", out var formatNode))
        {
            if (!TryReadString(formatNode, out var format) || string.IsNullOrWhiteSpace(format))
                errors.Add($"{path}.format: expected non-empty string");
            else if (strictProfile && !SupportedStrictFormats.Contains(format))
                errors.Add($"{path}.format: format '{format}' is not supported by strict structured output");
        }

        if (obj.ContainsKey("uniqueItems") && !IsBoolean(obj["uniqueItems"]))
            errors.Add($"{path}.uniqueItems: expected boolean");

        foreach (var definitionsKeyword in new[] { "$defs", "definitions" })
        {
            if (!obj.TryGetPropertyValue(definitionsKeyword, out var definitionsNode))
                continue;
            if (definitionsNode is not JsonObject definitions)
            {
                errors.Add($"{path}.{definitionsKeyword}: expected object");
                continue;
            }
            foreach (var (name, definition) in definitions)
            {
                statistics.TotalNameAndEnumLength += name.Length;
                ValidateSchemaNode(definition, root, $"{path}.{definitionsKeyword}.{name}", false, strictProfile, errors, statistics, depth + 1);
            }
        }

        if (strictProfile)
        {
            foreach (var keyword in UnsupportedStrictKeywords)
                if (obj.ContainsKey(keyword))
                    errors.Add($"{path}.{keyword}: keyword is not supported by strict structured output");
        }


        foreach (var keyword in UnsupportedRuntimeKeywords)
            if (obj.ContainsKey(keyword))
                errors.Add($"{path}.{keyword}: keyword is not supported by GnOuGo runtime schema validation");
    }

    private static HashSet<string> ReadDeclaredTypes(JsonObject obj, string path, List<string> errors)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!obj.TryGetPropertyValue("type", out var typeNode))
            return result;

        if (typeNode is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName) && typeName != null)
        {
            AddType(typeName, path, result, errors);
            return result;
        }

        if (typeNode is JsonArray types && types.Count > 0)
        {
            foreach (var candidate in types)
            {
                if (candidate is JsonValue candidateValue && candidateValue.TryGetValue<string>(out var candidateName) && candidateName != null)
                    AddType(candidateName, path, result, errors);
                else
                    errors.Add($"{path}.type: type arrays must contain only strings");
            }
            return result;
        }

        errors.Add($"{path}.type: expected a JSON type string or non-empty string array");
        return result;
    }

    private static void AddType(string typeName, string path, HashSet<string> result, List<string> errors)
    {
        if (!JsonTypes.Contains(typeName))
            errors.Add($"{path}.type: unknown JSON type '{typeName}'");
        else if (!result.Add(typeName))
            errors.Add($"{path}.type: duplicate type '{typeName}'");
    }

    private static void ValidateRequired(JsonObject obj, string path, bool strictProfile, List<string> errors)
    {
        HashSet<string>? requiredNames = null;
        if (obj.TryGetPropertyValue("required", out var requiredNode))
        {
            if (requiredNode is not JsonArray required)
            {
                errors.Add($"{path}.required: expected string array");
            }
            else
            {
                requiredNames = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < required.Count; i++)
                {
                    if (required[i] is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                        errors.Add($"{path}.required[{i}]: expected non-empty string");
                    else if (!requiredNames.Add(name))
                        errors.Add($"{path}.required: duplicate property '{name}'");
                }
            }
        }

        if (obj["properties"] is not JsonObject properties)
            return;

        if (requiredNames != null)
            foreach (var requiredName in requiredNames)
                if (!properties.ContainsKey(requiredName))
                    errors.Add($"{path}.required: property '{requiredName}' is not declared in properties");

        if (!strictProfile)
            return;

        requiredNames ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in properties.Select(static property => property.Key))
            if (!requiredNames.Contains(propertyName))
                errors.Add($"{path}.required: strict object schemas must include property '{propertyName}'");
    }

    private static bool IsObjectSchema(JsonObject obj)
    {
        if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName))
            return string.Equals(typeName, "object", StringComparison.Ordinal);
        if (obj["type"] is JsonArray types)
            return types.Any(type => type is JsonValue value && value.TryGetValue<string>(out var name) && name == "object");
        return obj.ContainsKey("properties");
    }

    private static bool DeclaresType(JsonObject obj, string expectedType)
    {
        if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName))
            return string.Equals(typeName, expectedType, StringComparison.Ordinal);
        return obj["type"] is JsonArray types
            && types.Any(type => type is JsonValue value
                && value.TryGetValue<string>(out var name)
                && string.Equals(name, expectedType, StringComparison.Ordinal));
    }

    private static bool IsBoolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out _);

    private static void ValidateNonNegativeIntegerKeyword(JsonObject obj, string path, string keyword, List<string> errors)
    {
        if (obj.ContainsKey(keyword) && (!TryReadInteger(obj[keyword], out var value) || value < 0))
            errors.Add($"{path}.{keyword}: expected non-negative integer");
    }

    private static void ValidateRange(JsonObject obj, string path, string minimumKeyword, string maximumKeyword, List<string> errors)
    {
        if (TryReadInteger(obj[minimumKeyword], out var minimum)
            && TryReadInteger(obj[maximumKeyword], out var maximum)
            && minimum > maximum)
            errors.Add($"{path}: {minimumKeyword} must be less than or equal to {maximumKeyword}");
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
        if (jsonValue.TryGetValue<decimal>(out value))
            return true;
        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            value = longValue;
            return true;
        }
        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }
        if (jsonValue.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue))
        {
            try { value = (decimal)doubleValue; return true; }
            catch (OverflowException) { return false; }
        }
        return false;
    }

    private static void ValidateInstanceNode(
        JsonNode? value,
        JsonObject schema,
        JsonObject root,
        string path,
        List<string> errors,
        HashSet<string> referenceStack)
    {
        if (TryReadString(schema["$ref"], out var reference))
        {
            if (!TryResolveLocalReference(root, reference, out var referencedSchema) || referencedSchema is not JsonObject referencedObject)
            {
                errors.Add($"{path}: unresolved schema reference '{reference}'");
                return;
            }
            var referenceKey = $"{reference}|{path}";
            if (!referenceStack.Add(referenceKey))
                return;
            ValidateInstanceNode(value, referencedObject, root, path, errors, referenceStack);
            referenceStack.Remove(referenceKey);
        }

        if (schema["allOf"] is JsonArray allOf)
            foreach (var variant in allOf.OfType<JsonObject>())
                ValidateInstanceNode(value, variant, root, path, errors, referenceStack);

        if (schema["anyOf"] is JsonArray anyOf && CountMatchingVariants(value, anyOf, root, referenceStack) == 0)
        {
            errors.Add($"{path}: value does not match any allowed schema variant");
            return;
        }

        if (schema["oneOf"] is JsonArray oneOf && CountMatchingVariants(value, oneOf, root, referenceStack) != 1)
        {
            errors.Add($"{path}: value must match exactly one allowed schema variant");
            return;
        }

        if (schema.TryGetPropertyValue("const", out var constant) && !JsonNode.DeepEquals(value, constant))
            errors.Add($"{path}: value must equal {constant?.ToJsonString() ?? "null"}");
        if (schema["enum"] is JsonArray allowed && !allowed.Any(candidate => JsonNode.DeepEquals(value, candidate)))
            errors.Add($"{path}: value is not included in enum");

        var applicableType = ReadApplicableType(schema, value);
        if (applicableType != null && !MatchesType(value, applicableType))
        {
            errors.Add($"{path}: expected {applicableType}");
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

    private static int CountMatchingVariants(JsonNode? value, JsonArray variants, JsonObject root, HashSet<string> referenceStack)
    {
        var matches = 0;
        foreach (var variant in variants.OfType<JsonObject>())
        {
            var variantErrors = new List<string>();
            ValidateInstanceNode(value, variant, root, "$", variantErrors, new HashSet<string>(referenceStack, StringComparer.Ordinal));
            if (variantErrors.Count == 0)
                matches++;
        }
        return matches;
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

    private static void ValidateObject(JsonNode? value, JsonObject schema, JsonObject root, string path, List<string> errors, HashSet<string> referenceStack)
    {
        if (value is not JsonObject obj)
            return;
        ValidateCount(obj.Count, schema, "minProperties", "maxProperties", path, "properties", errors);
        if (schema["required"] is JsonArray required)
            foreach (var requiredName in required.OfType<JsonValue>().Select(node => node.TryGetValue<string>(out var name) ? name : null).Where(static name => name != null))
                if (!obj.ContainsKey(requiredName!))
                    errors.Add($"{path}.{requiredName}: missing required property");

        var properties = schema["properties"] as JsonObject;
        foreach (var (name, childValue) in obj)
        {
            if (properties != null && properties.TryGetPropertyValue(name, out var childSchema) && childSchema is JsonObject childObject)
            {
                ValidateInstanceNode(childValue, childObject, root, $"{path}.{name}", errors, referenceStack);
                continue;
            }
            if (schema["additionalProperties"] is JsonValue additionalValue
                && additionalValue.TryGetValue<bool>(out var additionalAllowed)
                && !additionalAllowed)
                errors.Add($"{path}.{name}: property is not allowed by schema");
            else if (schema["additionalProperties"] is JsonObject additionalSchema)
                ValidateInstanceNode(childValue, additionalSchema, root, $"{path}.{name}", errors, referenceStack);
        }
    }

    private static void ValidateArray(JsonNode? value, JsonObject schema, JsonObject root, string path, List<string> errors, HashSet<string> referenceStack)
    {
        if (value is not JsonArray array)
            return;
        ValidateCount(array.Count, schema, "minItems", "maxItems", path, "items", errors);
        if (schema["uniqueItems"] is JsonValue uniqueValue && uniqueValue.TryGetValue<bool>(out var unique) && unique)
            for (var i = 0; i < array.Count; i++)
                for (var j = i + 1; j < array.Count; j++)
                    if (JsonNode.DeepEquals(array[i], array[j]))
                        errors.Add($"{path}: items at indexes {i} and {j} must be unique");
        if (schema["items"] is JsonObject itemSchema)
            for (var i = 0; i < array.Count; i++)
                ValidateInstanceNode(array[i], itemSchema, root, $"{path}[{i}]", errors, referenceStack);
    }

    private static void ValidateString(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || text == null)
            return;
        ValidateCount(text.Length, schema, "minLength", "maxLength", path, "characters", errors);
        if (TryReadString(schema["pattern"], out var pattern))
            try
            {
                if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    errors.Add($"{path}: string does not match pattern '{pattern}'");
            }
            catch (ArgumentException) { errors.Add($"{path}: schema contains an invalid pattern"); }
    }

    private static void ValidateNumber(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        if (!TryReadDecimal(value, out var number))
            return;
        if (TryReadDecimal(schema["minimum"], out var minimum) && number < minimum) errors.Add($"{path}: number must be >= {minimum}");
        if (TryReadDecimal(schema["maximum"], out var maximum) && number > maximum) errors.Add($"{path}: number must be <= {maximum}");
        if (TryReadDecimal(schema["exclusiveMinimum"], out var exclusiveMinimum) && number <= exclusiveMinimum) errors.Add($"{path}: number must be > {exclusiveMinimum}");
        if (TryReadDecimal(schema["exclusiveMaximum"], out var exclusiveMaximum) && number >= exclusiveMaximum) errors.Add($"{path}: number must be < {exclusiveMaximum}");
        if (TryReadDecimal(schema["multipleOf"], out var multipleOf) && multipleOf > 0 && number % multipleOf != 0) errors.Add($"{path}: number must be a multiple of {multipleOf}");
    }

    private static void ValidateCount(int count, JsonObject schema, string minimumKeyword, string maximumKeyword, string path, string unit, List<string> errors)
    {
        if (TryReadInteger(schema[minimumKeyword], out var minimum) && count < minimum) errors.Add($"{path}: must contain at least {minimum} {unit}");
        if (TryReadInteger(schema[maximumKeyword], out var maximum) && count > maximum) errors.Add($"{path}: must contain at most {maximum} {unit}");
    }

    private static bool TryResolveLocalReference(JsonObject root, string reference, out JsonNode? resolved)
    {
        resolved = null;
        if (!reference.StartsWith('#'))
            return false;
        if (reference == "#")
        {
            resolved = root;
            return true;
        }
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            return false;

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

    private sealed class StrictSchemaStatistics
    {
        public int PropertyCount { get; set; }
        public int MaximumDepth { get; set; }
        public int EnumValueCount { get; set; }
        public int TotalNameAndEnumLength { get; set; }
    }
}
