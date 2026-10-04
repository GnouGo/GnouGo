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

    private static readonly string[] UnsupportedRuntimeKeywords =
    {
        "not", "dependentSchemas",
        "patternProperties", "contains", "minContains", "maxContains",
        "propertyNames", "unevaluatedProperties", "unevaluatedItems"
    };

    private static readonly HashSet<string> SupportedStrictFormats = new(StringComparer.Ordinal)
    {
        "date-time", "time", "date", "duration", "email", "hostname", "ipv4", "ipv6", "uuid"
    };

    private static readonly HashSet<string> SupportedStrictKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "$defs", "definitions", "$ref", "type", "title", "description",
        "default", "examples", "deprecated", "readOnly", "writeOnly", "properties", "required", "additionalProperties",
        "items", "anyOf", "enum", "const", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "format", "minItems", "maxItems"
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
            foreach (var (path, target) in statistics.References)
                if (target is null || !statistics.SchemaNodes.Contains(target))
                    errors.Add($"{path}.$ref: reference must target a supported schema position");
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
        => JsonSchemaInstanceValidator.ValidateInstance(instance, schema);

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
        foreach (var keyword in new[] { "if", "then", "else" })
            if (obj[keyword] is JsonObject conditionalSchema)
                NormalizeSchema(conditionalSchema);
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
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        {
            if (Math.Truncate(value) == value)
            {
                if (value is >= int.MinValue and <= int.MaxValue)
                {
                    obj[keyword] = JsonValue.Create((int)value);
                    return;
                }

                if (value is >= -9223372036854775808d and < 9223372036854775808d)
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
        if (!isRoot && !strictProfile && IsBoolean(schema)) return;
        if (schema is not JsonObject obj)
        {
            errors.Add($"{path}: schema must be an object");
            return;
        }

        statistics.SchemaNodes.Add(obj);

        if (obj.TryGetPropertyValue("$ref", out var referenceNode))
        {
            if (!TryReadString(referenceNode, out var reference) || string.IsNullOrWhiteSpace(reference))
                errors.Add($"{path}.$ref: expected a non-empty string");
            else if (!TryResolveLocalReference(root, reference, out var target))
                errors.Add($"{path}.$ref: unresolved or unsupported reference '{reference}'; only local '#' references are supported");
            else if (strictProfile) statistics.References.Add((path, target));
        }

        var declaredTypes = ReadDeclaredTypes(obj, path, errors);
        if (strictProfile && declaredTypes.Count == 0 && !obj.ContainsKey("$ref") && !obj.ContainsKey("anyOf"))
            errors.Add($"{path}: strict schemas require a type, a reference or anyOf");
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
        if (obj.TryGetPropertyValue("prefixItems", out var prefix))
        {
            if (prefix is not JsonArray { Count: > 0 } tuple) errors.Add($"{path}.prefixItems: expected non-empty schema array");
            else for (var index = 0; index < tuple.Count; index++)
                ValidateSchemaNode(tuple[index], root, $"{path}.prefixItems[{index}]", false, strictProfile, errors, statistics, depth + 1);
        }

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

        foreach (var keyword in new[] { "if", "then", "else" })
        {
            if (!obj.TryGetPropertyValue(keyword, out var conditionalNode))
                continue;
            if (conditionalNode is not JsonObject)
            {
                errors.Add($"{path}.{keyword}: expected schema object");
                continue;
            }

            ValidateSchemaNode(conditionalNode, root, $"{path}.{keyword}", false, strictProfile, errors, statistics, depth + 1);
        }

        if (obj.TryGetPropertyValue("dependentRequired", out var dependentRequiredNode))
        {
            if (dependentRequiredNode is not JsonObject dependentRequired)
            {
                errors.Add($"{path}.dependentRequired: expected object");
            }
            else
            {
                foreach (var (propertyName, dependenciesNode) in dependentRequired)
                {
                    if (dependenciesNode is not JsonArray dependencies)
                    {
                        errors.Add($"{path}.dependentRequired.{propertyName}: expected string array");
                        continue;
                    }

                    var seenDependencies = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < dependencies.Count; i++)
                    {
                        if (dependencies[i] is not JsonValue dependencyValue
                            || !dependencyValue.TryGetValue<string>(out var dependency)
                            || string.IsNullOrWhiteSpace(dependency))
                        {
                            errors.Add($"{path}.dependentRequired.{propertyName}[{i}]: expected non-empty string");
                        }
                        else if (!seenDependencies.Add(dependency))
                        {
                            errors.Add($"{path}.dependentRequired.{propertyName}: duplicate property '{dependency}'");
                        }
                    }
                }
            }
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
            if (obj.ContainsKey(keyword) && !JsonSchemaInstanceValidator.TryReadNumber(obj[keyword], out _))
                errors.Add($"{path}.{keyword}: expected number");
        if (JsonSchemaInstanceValidator.TryReadNumber(obj["multipleOf"], out var multipleOf) && multipleOf <= 0)
            errors.Add($"{path}.multipleOf: expected a number greater than zero");
        if (JsonSchemaInstanceValidator.TryReadNumber(obj["minimum"], out _)
            && JsonSchemaInstanceValidator.TryReadNumber(obj["maximum"], out _)
            && JsonSchemaInstanceValidator.CompareNumbers(obj["minimum"]!, obj["maximum"]!) > 0)
            errors.Add($"{path}: minimum must be less than or equal to maximum");

        if (obj["pattern"] is JsonValue patternValue && patternValue.TryGetValue<string>(out var pattern) && pattern != null)
        {
            if (strictProfile)
            {
                var compatibility = statistics.Patterns.Check(pattern);
                if (compatibility != StructuredOutputPatterns.Compatibility.Portable)
                    errors.Add($"{path}.pattern: " + (compatibility == StructuredOutputPatterns.Compatibility.Invalid
                        ? "invalid regular expression" : "regular expression is not portable to strict structured output"));
            }
            else
            {
                try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException) { errors.Add($"{path}.pattern: invalid regular expression"); }
            }
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
            foreach (var keyword in obj.Select(p => p.Key))
                if (!SupportedStrictKeywords.Contains(keyword))
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
        if (node is not JsonValue scalar) return false;
        if (scalar.TryGetValue<long>(out value)) return true;
        if (scalar.TryGetValue<int>(out var integer)) { value = integer; return true; }
        if (scalar.TryGetValue<decimal>(out var number) && number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue)
        { value = (long)number; return true; }
        // The upper bound is exclusive: double cannot represent Int64.MaxValue exactly.
        if (scalar.TryGetValue<double>(out var floating) && double.IsFinite(floating) && floating == Math.Truncate(floating) &&
            floating >= -9223372036854775808d && floating < 9223372036854775808d)
        { value = (long)floating; return true; }
        return false;
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
        internal StructuredOutputPatterns Patterns { get; } = new();
        internal HashSet<JsonNode> SchemaNodes { get; } = new(ReferenceEqualityComparer.Instance);
        internal List<(string Path, JsonNode? Target)> References { get; } = [];
        public int PropertyCount { get; set; }
        public int MaximumDepth { get; set; }
        public int EnumValueCount { get; set; }
        public int TotalNameAndEnumLength { get; set; }
    }
}
