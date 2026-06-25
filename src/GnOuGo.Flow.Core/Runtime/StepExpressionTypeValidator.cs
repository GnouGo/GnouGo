using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed record StepExpressionTypeMismatch(
    string Field,
    string Expression,
    string ExpectedType,
    string ActualType,
    string Message);

/// <summary>
/// Conservative static type inference for interpolated step inputs. Unknown or opaque
/// expressions are deliberately accepted and remain protected by runtime validation.
/// </summary>
internal static class StepExpressionTypeValidator
{
    private static readonly Regex ExactExpression = new(
        @"^\s*\$\{(?<expression>[\s\S]*)\}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InputReference = new(
        @"^(?:data\.)?inputs\.([A-Za-z_][A-Za-z0-9_-]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StepReference = new(
        @"^(?:data\.)?steps\.([A-Za-z_][A-Za-z0-9_-]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceExpression = new(
        @"^(?:data\.)?(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonNullComparison = new(
        @"(?:(?<left>(?:data\.)?(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:!==|!=)\s*null|null\s*(?:!==|!=)\s*(?<right>(?:data\.)?(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExistsCall = new(
        @"^(?:functions\.)?exists\(\s*(?<reference>(?:data\.)?(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FunctionCall = new(
        @"^(?:functions\.)?([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IntegerLiteral = new(@"^[+-]?\d+$", RegexOptions.Compiled);
    private static readonly Regex NumberLiteral = new(@"^[+-]?(?:\d+\.\d*|\d*\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.Compiled);

    public static IReadOnlyList<StepExpressionTypeMismatch> ValidateInput(
        JsonNode? input,
        JsonObject inputSchema,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlyDictionary<string, JsonNode?> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        var mismatches = new List<StepExpressionTypeMismatch>();
        ValidateNode(input, inputSchema, "input", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
        return mismatches;
    }

    public static JsonObject? InferValueSchema(
        JsonNode? value,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlyDictionary<string, JsonNode?> knownStepOutputs)
    {
        if (value == null)
            return TypeSchema("null");
        if (value is JsonObject obj)
        {
            var properties = new JsonObject();
            foreach (var (name, child) in obj)
                properties[name] = InferValueSchema(child, workflowInputs, knownStepOutputs) ?? OpaqueSchema();
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            };
        }
        if (value is JsonArray array)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = array.Count == 0
                    ? OpaqueSchema()
                    : InferValueSchema(array[0], workflowInputs, knownStepOutputs) ?? OpaqueSchema()
            };
        }
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return text?.Contains("${", StringComparison.Ordinal) == true
                    ? InferInterpolatedString(text, workflowInputs, knownStepOutputs) ?? OpaqueSchema()
                    : TypeSchema("string");
            }
            if (scalar.TryGetValue<bool>(out _))
                return TypeSchema("boolean");
            if (scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _))
                return TypeSchema("integer");
            if (scalar.TryGetValue<decimal>(out _) || scalar.TryGetValue<double>(out _))
                return TypeSchema("number");
        }
        return null;
    }

    public static StepExpressionTypeMismatch? ValidateExpression(
        string? expression,
        string field,
        JsonObject expectedSchema,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlyDictionary<string, JsonNode?> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        if (string.IsNullOrWhiteSpace(expression) || !expression.Contains("${", StringComparison.Ordinal))
            return null;

        var inferred = InferInterpolatedString(expression, workflowInputs, knownStepOutputs, nonNullReferences);
        if (inferred == null || IsCompatible(inferred, expectedSchema))
            return null;

        var expected = DescribeExpected(expectedSchema);
        var actual = DescribeActual(inferred);
        return new StepExpressionTypeMismatch(
            field,
            expression,
            expected,
            actual,
            $"Expression assigned to '{field}' resolves to {actual}, but the contract requires {expected}.");
    }

    public static IReadOnlySet<string> InferNonNullReferencesFromGuard(string? guard)
    {
        if (string.IsNullOrWhiteSpace(guard) || !guard.Contains("${", StringComparison.Ordinal))
            return EmptyStringSet.Value;

        var exact = ExactExpression.Match(guard);
        if (!exact.Success)
            return EmptyStringSet.Value;

        var expression = exact.Groups["expression"].Value.Trim();
        if (expression.Length == 0 || expression.Contains("||", StringComparison.Ordinal))
            return EmptyStringSet.Value;

        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawTerm in expression.Split(new[] { "&&" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = TrimEnclosingParentheses(rawTerm);
            if (TryNormalizeReference(term, out var directReference))
            {
                references.Add(directReference);
                continue;
            }

            var exists = ExistsCall.Match(term);
            if (exists.Success && TryNormalizeReference(exists.Groups["reference"].Value, out var existsReference))
                references.Add(existsReference);

            foreach (Match comparison in NonNullComparison.Matches(term))
            {
                if (comparison.Groups["left"].Success && TryNormalizeReference(comparison.Groups["left"].Value, out var leftReference))
                    references.Add(leftReference);
                if (comparison.Groups["right"].Success && TryNormalizeReference(comparison.Groups["right"].Value, out var rightReference))
                    references.Add(rightReference);
            }
        }

        return references.Count == 0 ? EmptyStringSet.Value : references;
    }

    public static JsonObject OutputDefSchema(OutputDef definition)
    {
        var type = NormalizeType(definition.Type);
        if (type == null)
            return OpaqueSchema();
        if (type == "array")
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = definition.Items == null ? OpaqueSchema() : OutputDefSchema(definition.Items)
            };
        if (type is "object" or "dictionary")
        {
            var properties = new JsonObject();
            if (definition.Properties != null)
            {
                foreach (var (name, child) in definition.Properties)
                    properties[name] = OutputDefSchema(child);
            }
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = definition.AdditionalProperties == null
                    ? type == "dictionary"
                    : OutputDefSchema(definition.AdditionalProperties)
            };
            if (definition.RequiredProperties is { Count: > 0 })
            {
                schema["required"] = new JsonArray(
                    definition.RequiredProperties
                        .Select(static name => (JsonNode?)JsonValue.Create(name))
                        .ToArray());
            }
            return schema;
        }
        return TypeSchema(type);
    }

    public static JsonObject InputsObjectSchema(IReadOnlyDictionary<string, InputDef>? inputs)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        if (inputs != null)
        {
            foreach (var (name, definition) in inputs)
            {
                properties[name] = InputDefSchema(definition);
                if (definition.Required)
                    required.Add((JsonNode?)JsonValue.Create(name));
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    public static JsonObject OutputsObjectSchema(IReadOnlyDictionary<string, OutputDef>? outputs)
    {
        var properties = new JsonObject();

        if (outputs != null)
        {
            foreach (var (name, definition) in outputs)
                properties[name] = OutputDefSchema(definition);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
    }

    private static void ValidateNode(
        JsonNode? value,
        JsonObject schema,
        string field,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlyDictionary<string, JsonNode?> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences,
        List<StepExpressionTypeMismatch> mismatches)
    {
        if (IsOpaque(schema))
            return;

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text?.Contains("${", StringComparison.Ordinal) == true)
        {
            var inferred = InferInterpolatedString(text, workflowInputs, knownStepOutputs, nonNullReferences);
            if (inferred != null && !IsCompatible(inferred, schema))
            {
                var expected = DescribeExpected(schema);
                var actual = DescribeActual(inferred);
                mismatches.Add(new StepExpressionTypeMismatch(
                    field,
                    text,
                    expected,
                    actual,
                    $"Expression assigned to '{field}' resolves to {actual}, but the step contract requires {expected}."));
            }
            return;
        }

        if (value is JsonObject obj && schema["type"]?.GetValue<string>() == "object")
        {
            var properties = schema["properties"] as JsonObject;
            foreach (var (name, child) in obj)
            {
                if (properties?[name] is JsonObject childSchema)
                    ValidateNode(child, childSchema, $"{field}.{name}", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
            }
            return;
        }

        if (value is JsonArray array
            && schema["type"]?.GetValue<string>() == "array"
            && schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
                ValidateNode(array[i], itemSchema, $"{field}[{i}]", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
        }
    }

    private static JsonObject? InferInterpolatedString(
        string text,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlyDictionary<string, JsonNode?> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        var exact = ExactExpression.Match(text);
        if (!exact.Success)
            return TypeSchema("string");

        var expression = exact.Groups["expression"].Value.Trim();
        if (expression.Length == 0)
            return null;

        var inputMatch = InputReference.Match(expression);
        if (inputMatch.Success && workflowInputs != null
            && workflowInputs.TryGetValue(inputMatch.Groups[1].Value, out var inputDef))
        {
            var reference = $"inputs.{inputMatch.Groups[1].Value}{inputMatch.Groups["path"].Value}";
            return ApplyNonNullNarrowing(
                ResolveSchemaPath(InputDefSchema(inputDef), SplitPath(inputMatch.Groups["path"].Value)),
                reference,
                nonNullReferences);
        }

        var stepMatch = StepReference.Match(expression);
        if (stepMatch.Success
            && knownStepOutputs.TryGetValue(stepMatch.Groups[1].Value, out var outputSchema))
        {
            var reference = $"steps.{stepMatch.Groups[1].Value}{stepMatch.Groups["path"].Value}";
            return ApplyNonNullNarrowing(
                ResolveSchemaPath(outputSchema as JsonObject, SplitPath(stepMatch.Groups["path"].Value)),
                reference,
                nonNullReferences);
        }

        if (expression is "true" or "false" || LooksBoolean(expression))
            return TypeSchema("boolean");
        if (expression == "null")
            return TypeSchema("null");
        if (IntegerLiteral.IsMatch(expression))
            return TypeSchema("integer");
        if (NumberLiteral.IsMatch(expression))
            return TypeSchema("number");
        if ((expression.StartsWith('"') && expression.EndsWith('"'))
            || (expression.StartsWith('\'') && expression.EndsWith('\''))
            || expression.StartsWith('`'))
            return TypeSchema("string");
        if (expression.StartsWith('['))
            return TypeSchema("array");
        if (expression.StartsWith('{'))
            return TypeSchema("object");

        var function = FunctionCall.Match(expression);
        if (function.Success)
        {
            return function.Groups[1].Value switch
            {
                "exists" or "contains" or "startsWith" or "endsWith" => TypeSchema("boolean"),
                "len" or "length" => TypeSchema("integer"),
                "toNumber" => TypeSchema("number"),
                "lower" or "upper" or "trim" or "replace" or "substring" or "json"
                    or "now" or "formatDate" or "base64" => TypeSchema("string"),
                "pick" or "omit" => TypeSchema("object"),
                _ => null
            };
        }

        return null;
    }

    private static JsonObject? ApplyNonNullNarrowing(
        JsonObject? schema,
        string reference,
        IReadOnlySet<string>? nonNullReferences)
    {
        if (schema == null || nonNullReferences == null || !nonNullReferences.Contains(reference))
            return schema;

        return RemoveNullType(schema);
    }

    private static JsonObject RemoveNullType(JsonObject schema)
    {
        if (IsOpaque(schema))
            return schema.DeepClone() as JsonObject ?? OpaqueSchema();

        var clone = schema.DeepClone() as JsonObject ?? OpaqueSchema();
        if (clone.ContainsKey("type") && clone["type"] == null)
        {
            return OpaqueSchema();
        }
        if (clone["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) && type != null)
        {
            if (string.Equals(type, "null", StringComparison.Ordinal))
                return OpaqueSchema();
        }
        else if (clone["type"] is JsonArray typeArray)
        {
            var remaining = typeArray
                .OfType<JsonValue>()
                .Select(static value => value.TryGetValue<string>(out var item) ? item : null)
                .Where(static item => item != null && !string.Equals(item, "null", StringComparison.Ordinal))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            clone["type"] = remaining.Length switch
            {
                0 => null,
                1 => JsonValue.Create(remaining[0]),
                _ => new JsonArray(remaining.Select(static item => (JsonNode?)JsonValue.Create(item)).ToArray())
            };

            if (remaining.Length == 0)
                return OpaqueSchema();
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (clone[keyword] is not JsonArray variants)
                continue;

            var narrowed = new JsonArray();
            foreach (var variant in variants)
            {
                if (variant is not JsonObject variantObject)
                {
                    narrowed.Add((JsonNode?)variant?.DeepClone());
                    continue;
                }

                var variantTypes = ReadTypes(variantObject);
                if (variantTypes.Count == 1 && variantTypes.Contains("null"))
                    continue;

                narrowed.Add((JsonNode?)RemoveNullType(variantObject));
            }

            if (narrowed.Count == 0)
                return OpaqueSchema();

            clone[keyword] = narrowed;
        }

        return clone;
    }

    private static bool TryNormalizeReference(string expression, out string reference)
    {
        reference = "";
        var normalized = TrimEnclosingParentheses(expression);
        if (!ReferenceExpression.IsMatch(normalized))
            return false;

        reference = normalized.StartsWith("data.", StringComparison.Ordinal)
            ? normalized["data.".Length..]
            : normalized;
        return true;
    }

    private static string TrimEnclosingParentheses(string value)
    {
        var current = value.Trim();
        while (current.Length >= 2
               && current[0] == '('
               && current[^1] == ')'
               && IsSingleParenthesizedExpression(current))
        {
            current = current[1..^1].Trim();
        }
        return current;
    }

    private static bool IsSingleParenthesizedExpression(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
                depth++;
            else if (value[i] == ')')
                depth--;

            if (depth == 0 && i < value.Length - 1)
                return false;
        }
        return depth == 0;
    }

    private static bool LooksBoolean(string expression)
    {
        if (expression.StartsWith('!'))
            return true;
        return expression.Contains("===", StringComparison.Ordinal)
            || expression.Contains("!==", StringComparison.Ordinal)
            || expression.Contains("==", StringComparison.Ordinal)
            || expression.Contains("!=", StringComparison.Ordinal)
            || expression.Contains(">=", StringComparison.Ordinal)
            || expression.Contains("<=", StringComparison.Ordinal)
            || expression.Contains(" > ", StringComparison.Ordinal)
            || expression.Contains(" < ", StringComparison.Ordinal)
            || expression.Contains("&&", StringComparison.Ordinal)
            || expression.Contains("||", StringComparison.Ordinal);
    }

    private static JsonObject InputDefSchema(InputDef definition)
    {
        var type = NormalizeType(definition.Type);
        if (type == null)
            return OpaqueSchema();

        if (type == "array")
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = definition.Items == null ? OpaqueSchema() : InputDefSchema(definition.Items)
            };

        if (type == "object")
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            if (definition.Properties != null)
            {
                foreach (var (name, child) in definition.Properties)
                {
                    properties[name] = InputDefSchema(child);
                    if (definition.RequiredProperties == null && child.Required)
                        required.Add((JsonNode?)JsonValue.Create(name));
                }
            }

            if (definition.RequiredProperties is { Count: > 0 })
            {
                required = new JsonArray(
                    definition.RequiredProperties
                        .Select(static name => (JsonNode?)JsonValue.Create(name))
                        .ToArray());
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = definition.AdditionalProperties == null
                    ? false
                    : InputDefSchema(definition.AdditionalProperties)
            };
            if (required.Count > 0)
                schema["required"] = required;
            return schema;
        }

        if (type == "dictionary")
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = definition.AdditionalProperties == null
                    ? OpaqueSchema()
                    : InputDefSchema(definition.AdditionalProperties)
            };
        }

        return TypeSchema(type);
    }

    private static string? NormalizeType(string? type) => type?.ToLowerInvariant() switch
    {
        "string" => "string",
        "number" => "number",
        "integer" => "integer",
        "boolean" or "bool" => "boolean",
        "array" => "array",
        "object" => "object",
        "dictionary" => "dictionary",
        "null" => "null",
        _ => null
    };

    private static JsonObject? ResolveSchemaPath(JsonObject? schema, IReadOnlyList<string> path)
    {
        var current = schema;
        foreach (var segment in path)
        {
            if (current == null || IsOpaque(current))
                return null;
            if (current["properties"] is JsonObject properties && properties[segment] is JsonObject child)
            {
                current = child;
                continue;
            }
            if (current["additionalProperties"] is JsonObject additional)
            {
                current = additional;
                continue;
            }
            return null;
        }
        return current?.DeepClone() as JsonObject;
    }

    private static IReadOnlyList<string> SplitPath(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsCompatible(JsonObject actual, JsonObject expected)
    {
        if (IsOpaque(actual) || IsOpaque(expected))
            return true;

        var actualTypes = ReadTypes(actual);
        var expectedTypes = ReadTypes(expected);
        if (actualTypes.Count == 0 || expectedTypes.Count == 0)
            return true;

        return actualTypes.All(actualType =>
            expectedTypes.Contains(actualType)
            || (actualType == "integer" && expectedTypes.Contains("number")));
    }

    private static HashSet<string> ReadTypes(JsonObject schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ContainsKey("type") && schema["type"] == null)
            types.Add("null");
        else if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) && type != null)
            types.Add(type);
        else if (schema["type"] is JsonArray typeArray)
        {
            foreach (var node in typeArray)
            {
                if (node == null)
                    types.Add("null");
                else if (node is JsonValue value && value.TryGetValue<string>(out var item) && item != null)
                    types.Add(item);
            }
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (schema[keyword] is not JsonArray variants)
                continue;
            foreach (var variant in variants)
            {
                if (variant is JsonObject variantSchema)
                    types.UnionWith(ReadTypes(variantSchema));
            }
        }
        return types;
    }

    private static string DescribeExpected(JsonObject schema)
    {
        var types = ReadTypes(schema).OrderBy(static type => type, StringComparer.Ordinal).ToArray();
        return types.Length == 0 ? "compatible" : string.Join(" or ", types);
    }

    private static string DescribeActual(JsonObject schema) => DescribeExpected(schema);

    private static bool IsOpaque(JsonObject schema) =>
        schema["x-gnougo-opaque"] is JsonValue value
        && value.TryGetValue<bool>(out var opaque)
        && opaque;

    private static JsonObject TypeSchema(string type) => new() { ["type"] = type };
    private static JsonObject OpaqueSchema() => new() { ["x-gnougo-opaque"] = true };

    private static class EmptyStringSet
    {
        public static readonly IReadOnlySet<string> Value = new HashSet<string>(StringComparer.Ordinal);
    }
}
