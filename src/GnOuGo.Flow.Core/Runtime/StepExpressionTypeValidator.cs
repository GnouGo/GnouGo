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
        var knownTypes = knownStepOutputs.ToDictionary(
            static pair => pair.Key,
            pair => FlowTypeDescriptorConverter.FromJsonSchema(pair.Value),
            StringComparer.Ordinal);

        return ValidateInput(
            input,
            FlowTypeDescriptorConverter.FromJsonSchema(inputSchema),
            FlowTypeDescriptorConverter.InputMap(workflowInputs),
            knownTypes,
            nonNullReferences);
    }

    public static IReadOnlyList<StepExpressionTypeMismatch> ValidateInput(
        JsonNode? input,
        FlowTypeDescriptor inputType,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        var mismatches = new List<StepExpressionTypeMismatch>();
        ValidateNode(input, inputType, "input", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
        return mismatches;
    }

    public static JsonObject? InferValueSchema(
        JsonNode? value,
        IReadOnlyDictionary<string, InputDef>? workflowInputs,
        IReadOnlyDictionary<string, JsonNode?> knownStepOutputs)
    {
        var knownTypes = knownStepOutputs.ToDictionary(
            static pair => pair.Key,
            pair => FlowTypeDescriptorConverter.FromJsonSchema(pair.Value),
            StringComparer.Ordinal);

        var inferred = InferValueType(value, FlowTypeDescriptorConverter.InputMap(workflowInputs), knownTypes);
        return inferred == null ? null : FlowTypeDescriptorConverter.ToRuntimeJsonSchema(inferred);
    }

    public static FlowTypeDescriptor? InferValueType(
        JsonNode? value,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs)
    {
        if (value == null)
            return FlowTypeDescriptor.Null;

        if (value is JsonObject obj)
        {
            var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
            foreach (var (name, child) in obj)
                properties[name] = new FlowPropertyDescriptor(
                    InferValueType(child, workflowInputs, knownStepOutputs) ?? FlowTypeDescriptor.Any,
                    Required: true);
            return FlowTypeDescriptor.Object(properties);
        }

        if (value is JsonArray array)
        {
            return FlowTypeDescriptor.Array(array.Count == 0
                ? FlowTypeDescriptor.Any
                : InferValueType(array[0], workflowInputs, knownStepOutputs) ?? FlowTypeDescriptor.Any);
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return text?.Contains("${", StringComparison.Ordinal) == true
                    ? InferInterpolatedStringType(text, workflowInputs, knownStepOutputs) ?? FlowTypeDescriptor.Any
                    : FlowTypeDescriptor.String;
            }
            if (scalar.TryGetValue<bool>(out _))
                return FlowTypeDescriptor.Boolean;
            if (scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _))
                return FlowTypeDescriptor.Integer;
            if (scalar.TryGetValue<decimal>(out _) || scalar.TryGetValue<double>(out _))
                return FlowTypeDescriptor.Number;
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
        var knownTypes = knownStepOutputs.ToDictionary(
            static pair => pair.Key,
            pair => FlowTypeDescriptorConverter.FromJsonSchema(pair.Value),
            StringComparer.Ordinal);

        return ValidateExpression(
            expression,
            field,
            FlowTypeDescriptorConverter.FromJsonSchema(expectedSchema),
            FlowTypeDescriptorConverter.InputMap(workflowInputs),
            knownTypes,
            nonNullReferences);
    }

    public static StepExpressionTypeMismatch? ValidateExpression(
        string? expression,
        string field,
        FlowTypeDescriptor expectedType,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        if (string.IsNullOrWhiteSpace(expression) || !expression.Contains("${", StringComparison.Ordinal))
            return null;

        var inferred = InferInterpolatedStringType(expression, workflowInputs, knownStepOutputs, nonNullReferences);
        if (inferred == null || inferred.IsCompatibleWith(expectedType))
            return null;

        var expected = expectedType.Describe();
        var actual = inferred.Describe();
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

    public static JsonObject OutputDefSchema(OutputDef definition) =>
        FlowTypeDescriptorConverter.ToRuntimeJsonSchema(FlowTypeDescriptorConverter.FromOutputDef(definition));

    public static FlowTypeDescriptor OutputDefType(OutputDef definition) =>
        FlowTypeDescriptorConverter.FromOutputDef(definition);

    public static JsonObject InputsObjectSchema(IReadOnlyDictionary<string, InputDef>? inputs) =>
        FlowTypeDescriptorConverter.ToRuntimeJsonSchema(FlowTypeDescriptorConverter.InputsObject(inputs));

    public static FlowTypeDescriptor InputsObjectType(IReadOnlyDictionary<string, InputDef>? inputs) =>
        FlowTypeDescriptorConverter.InputsObject(inputs);

    public static JsonObject OutputsObjectSchema(IReadOnlyDictionary<string, OutputDef>? outputs) =>
        FlowTypeDescriptorConverter.ToRuntimeJsonSchema(FlowTypeDescriptorConverter.OutputsObject(outputs));

    public static FlowTypeDescriptor OutputsObjectType(IReadOnlyDictionary<string, OutputDef>? outputs) =>
        FlowTypeDescriptorConverter.OutputsObject(outputs);

    private static void ValidateNode(
        JsonNode? value,
        FlowTypeDescriptor expectedType,
        string field,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences,
        List<StepExpressionTypeMismatch> mismatches)
    {
        if (expectedType.IsOpaque)
            return;

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text?.Contains("${", StringComparison.Ordinal) == true)
        {
            var inferred = InferInterpolatedStringType(text, workflowInputs, knownStepOutputs, nonNullReferences);
            if (inferred != null && !inferred.IsCompatibleWith(expectedType))
            {
                var expected = expectedType.Describe();
                var actual = inferred.Describe();
                mismatches.Add(new StepExpressionTypeMismatch(
                    field,
                    text,
                    expected,
                    actual,
                    $"Expression assigned to '{field}' resolves to {actual}, but the step contract requires {expected}."));
            }
            return;
        }

        var objectType = SelectObjectCompatibleType(value, expectedType);
        if (value is JsonObject obj && objectType != null)
        {
            foreach (var (name, child) in obj)
            {
                if (objectType.Properties.TryGetValue(name, out var property))
                    ValidateNode(child, property.Type, $"{field}.{name}", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
                else if (objectType.AdditionalProperties != null)
                    ValidateNode(child, objectType.AdditionalProperties, $"{field}.{name}", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
            }
            return;
        }

        var arrayType = SelectArrayCompatibleType(value, expectedType);
        if (value is JsonArray array && arrayType?.Items != null)
        {
            for (var i = 0; i < array.Count; i++)
                ValidateNode(array[i], arrayType.Items, $"{field}[{i}]", workflowInputs, knownStepOutputs, nonNullReferences, mismatches);
        }
    }

    private static FlowTypeDescriptor? SelectObjectCompatibleType(JsonNode? value, FlowTypeDescriptor expectedType)
    {
        if (value is not JsonObject)
            return null;
        if (expectedType.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary)
            return expectedType;
        if (expectedType.Kind == FlowTypeKind.Union)
            return expectedType.Variants.FirstOrDefault(static variant => variant.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary);
        return null;
    }

    private static FlowTypeDescriptor? SelectArrayCompatibleType(JsonNode? value, FlowTypeDescriptor expectedType)
    {
        if (value is not JsonArray)
            return null;
        if (expectedType.Kind == FlowTypeKind.Array)
            return expectedType;
        if (expectedType.Kind == FlowTypeKind.Union)
            return expectedType.Variants.FirstOrDefault(static variant => variant.Kind == FlowTypeKind.Array);
        return null;
    }

    private static FlowTypeDescriptor? InferInterpolatedStringType(
        string text,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        var exact = ExactExpression.Match(text);
        if (!exact.Success)
            return FlowTypeDescriptor.String;

        var expression = exact.Groups["expression"].Value.Trim();
        if (expression.Length == 0)
            return null;

        var inputMatch = InputReference.Match(expression);
        if (inputMatch.Success && workflowInputs != null
            && workflowInputs.TryGetValue(inputMatch.Groups[1].Value, out var inputType))
        {
            var reference = $"inputs.{inputMatch.Groups[1].Value}{inputMatch.Groups["path"].Value}";
            return ApplyNonNullNarrowing(
                inputType.ResolvePath(SplitPath(inputMatch.Groups["path"].Value)),
                reference,
                nonNullReferences);
        }

        var stepMatch = StepReference.Match(expression);
        if (stepMatch.Success && knownStepOutputs.TryGetValue(stepMatch.Groups[1].Value, out var outputType))
        {
            var reference = $"steps.{stepMatch.Groups[1].Value}{stepMatch.Groups["path"].Value}";
            return ApplyNonNullNarrowing(
                outputType.ResolvePath(SplitPath(stepMatch.Groups["path"].Value)),
                reference,
                nonNullReferences);
        }

        if (expression is "true" or "false" || LooksBoolean(expression))
            return FlowTypeDescriptor.Boolean;
        if (expression == "null")
            return FlowTypeDescriptor.Null;
        if (IntegerLiteral.IsMatch(expression))
            return FlowTypeDescriptor.Integer;
        if (NumberLiteral.IsMatch(expression))
            return FlowTypeDescriptor.Number;
        if ((expression.StartsWith('"') && expression.EndsWith('"'))
            || (expression.StartsWith('\'') && expression.EndsWith('\''))
            || expression.StartsWith('`'))
            return FlowTypeDescriptor.String;
        if (expression.StartsWith('['))
            return FlowTypeDescriptor.Array();
        if (expression.StartsWith('{'))
            return FlowTypeDescriptor.Object(allowsAdditionalProperties: true);

        var function = FunctionCall.Match(expression);
        if (function.Success)
        {
            return function.Groups[1].Value switch
            {
                "exists" or "contains" or "startsWith" or "endsWith" => FlowTypeDescriptor.Boolean,
                "len" or "length" => FlowTypeDescriptor.Integer,
                "toNumber" => FlowTypeDescriptor.Number,
                "lower" or "upper" or "trim" or "replace" or "substring" or "json"
                    or "now" or "formatDate" or "base64" => FlowTypeDescriptor.String,
                "pick" or "omit" => FlowTypeDescriptor.Object(allowsAdditionalProperties: true),
                _ => null
            };
        }

        return null;
    }

    private static FlowTypeDescriptor? ApplyNonNullNarrowing(
        FlowTypeDescriptor? type,
        string reference,
        IReadOnlySet<string>? nonNullReferences)
    {
        if (type == null || nonNullReferences == null || !nonNullReferences.Contains(reference))
            return type;

        return type.RemoveNull();
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

    private static IReadOnlyList<string> SplitPath(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static class EmptyStringSet
    {
        public static readonly IReadOnlySet<string> Value = new HashSet<string>(StringComparer.Ordinal);
    }
}
