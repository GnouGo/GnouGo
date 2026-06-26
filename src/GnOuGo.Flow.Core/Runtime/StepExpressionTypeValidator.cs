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
    private static readonly Regex DataVariableReference = new(
        @"^(?:data\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceExpression = new(
        @"^(?:data\.)?(?:(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonNullComparison = new(
        @"(?:(?<left>(?:data\.)?(?:(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*))\s*(?:!==|!=)\s*null|null\s*(?:!==|!=)\s*(?<right>(?:data\.)?(?:(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExistsCall = new(
        @"^(?:functions\.)?exists\(\s*(?<reference>(?:data\.)?(?:(?:inputs|steps)\.[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*))\s*\)$",
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
            dataVariables: null,
            nonNullReferences);
    }

    public static IReadOnlyList<StepExpressionTypeMismatch> ValidateInput(
        JsonNode? input,
        FlowTypeDescriptor inputType,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables = null,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        var mismatches = new List<StepExpressionTypeMismatch>();
        ValidateNode(input, inputType, "input", workflowInputs, knownStepOutputs, dataVariables, nonNullReferences, mismatches);
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
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables = null)
    {
        if (value == null)
            return FlowTypeDescriptor.Null;

        if (value is JsonObject obj)
        {
            var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
            foreach (var (name, child) in obj)
                properties[name] = new FlowPropertyDescriptor(
                    InferValueType(child, workflowInputs, knownStepOutputs, dataVariables) ?? FlowTypeDescriptor.Any,
                    Required: true);
            return FlowTypeDescriptor.Object(properties);
        }

        if (value is JsonArray array)
        {
            var itemTypes = array
                .Select(item => InferValueType(item, workflowInputs, knownStepOutputs, dataVariables) ?? FlowTypeDescriptor.Any)
                .ToArray();
            return FlowTypeDescriptor.Array(itemTypes.Length == 0
                ? FlowTypeDescriptor.Any
                : FlowTypeDescriptor.Union(itemTypes));
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return text?.Contains("${", StringComparison.Ordinal) == true
                    ? InferInterpolatedStringType(text, workflowInputs, knownStepOutputs, dataVariables) ?? FlowTypeDescriptor.Any
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
            dataVariables: null,
            nonNullReferences);
    }

    public static StepExpressionTypeMismatch? ValidateExpression(
        string? expression,
        string field,
        FlowTypeDescriptor expectedType,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables = null,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        if (string.IsNullOrWhiteSpace(expression) || !expression.Contains("${", StringComparison.Ordinal))
            return null;

        var semanticMismatch = ValidateInterpolatedExpressionSemantics(
            expression,
            field,
            workflowInputs,
            knownStepOutputs,
            dataVariables,
            nonNullReferences);
        if (semanticMismatch != null)
            return semanticMismatch;

        var inferred = InferInterpolatedStringType(expression, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
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
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables,
        IReadOnlySet<string>? nonNullReferences,
        List<StepExpressionTypeMismatch> mismatches)
    {
        if (expectedType.IsOpaque)
            return;

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text?.Contains("${", StringComparison.Ordinal) == true)
        {
            var semanticMismatch = ValidateInterpolatedExpressionSemantics(
                text,
                field,
                workflowInputs,
                knownStepOutputs,
                dataVariables,
                nonNullReferences);
            if (semanticMismatch != null)
                mismatches.Add(semanticMismatch);

            var inferred = InferInterpolatedStringType(text, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
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
                    ValidateNode(child, property.Type, $"{field}.{name}", workflowInputs, knownStepOutputs, dataVariables, nonNullReferences, mismatches);
                else if (objectType.AdditionalProperties != null)
                    ValidateNode(child, objectType.AdditionalProperties, $"{field}.{name}", workflowInputs, knownStepOutputs, dataVariables, nonNullReferences, mismatches);
            }
            return;
        }

        var arrayType = SelectArrayCompatibleType(value, expectedType);
        if (value is JsonArray array && arrayType?.Items != null)
        {
            for (var i = 0; i < array.Count; i++)
                ValidateNode(array[i], arrayType.Items, $"{field}[{i}]", workflowInputs, knownStepOutputs, dataVariables, nonNullReferences, mismatches);
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
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables = null,
        IReadOnlySet<string>? nonNullReferences = null)
    {
        var exact = ExactExpression.Match(text);
        if (!exact.Success)
            return FlowTypeDescriptor.String;

        var expression = exact.Groups["expression"].Value.Trim();
        if (expression.Length == 0)
            return null;

        return InferExpressionType(expression, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
    }

    private static FlowTypeDescriptor? InferExpressionType(
        string expression,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables,
        IReadOnlySet<string>? nonNullReferences)
    {
        expression = TrimEnclosingParentheses(expression);
        if (expression.Length == 0)
            return null;

        if (TrySplitTopLevelTernary(expression, out var ternary))
        {
            var trueType = InferExpressionType(ternary.WhenTrue, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
            var falseType = InferExpressionType(ternary.WhenFalse, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
            return trueType == null || falseType == null
                ? null
                : FlowTypeDescriptor.Union(new[] { trueType, falseType });
        }

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

        var variableMatch = DataVariableReference.Match(expression);
        if (variableMatch.Success
            && variableMatch.Groups["name"].Value is { } variableName
            && variableName is not ("inputs" or "steps")
            && dataVariables != null
            && dataVariables.TryGetValue(variableName, out var variableType))
        {
            var reference = $"{variableName}{variableMatch.Groups["path"].Value}";
            return ApplyNonNullNarrowing(
                variableType.ResolvePath(SplitPath(variableMatch.Groups["path"].Value)),
                reference,
                nonNullReferences);
        }

        if (expression is "true" or "false")
            return FlowTypeDescriptor.Boolean;
        if (expression == "null")
            return FlowTypeDescriptor.Null;
        if (IntegerLiteral.IsMatch(expression))
            return FlowTypeDescriptor.Integer;
        if (NumberLiteral.IsMatch(expression))
            return FlowTypeDescriptor.Number;
        if (IsStringLiteral(expression))
            return FlowTypeDescriptor.String;
        if (expression.StartsWith('['))
            return FlowTypeDescriptor.Array();
        if (expression.StartsWith('{'))
            return FlowTypeDescriptor.Object(allowsAdditionalProperties: true);

        if (LooksBoolean(expression))
            return FlowTypeDescriptor.Boolean;

        if (TryInferStringConcatenation(expression, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences, out var concatenationType))
            return concatenationType;

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

    private static StepExpressionTypeMismatch? ValidateInterpolatedExpressionSemantics(
        string text,
        string field,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables,
        IReadOnlySet<string>? nonNullReferences)
    {
        var exact = ExactExpression.Match(text);
        if (!exact.Success)
            return null;

        var expression = exact.Groups["expression"].Value.Trim();
        return ValidateTernaryConditions(expression, text, field, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
    }

    private static StepExpressionTypeMismatch? ValidateTernaryConditions(
        string expression,
        string originalExpression,
        string field,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables,
        IReadOnlySet<string>? nonNullReferences)
    {
        expression = TrimEnclosingParentheses(expression);
        if (!TrySplitTopLevelTernary(expression, out var ternary))
            return null;

        var conditionType = InferExpressionType(ternary.Condition, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
        if (conditionType != null && !conditionType.IsCompatibleWith(FlowTypeDescriptor.Boolean))
        {
            var actual = conditionType.Describe();
            return new StepExpressionTypeMismatch(
                field,
                originalExpression,
                FlowTypeDescriptor.Boolean.Describe(),
                actual,
                $"Ternary condition in '{field}' resolves to {actual}, but ternary conditions must be boolean.");
        }

        return ValidateTernaryConditions(ternary.WhenTrue, originalExpression, field, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences)
               ?? ValidateTernaryConditions(ternary.WhenFalse, originalExpression, field, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
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

    private sealed record TernaryExpressionParts(string Condition, string WhenTrue, string WhenFalse);

    private static bool TrySplitTopLevelTernary(string expression, out TernaryExpressionParts ternary)
    {
        ternary = default!;
        var questionIndex = FindTopLevelTernaryQuestion(expression);
        if (questionIndex < 0)
            return false;

        var colonIndex = FindMatchingTernaryColon(expression, questionIndex + 1);
        if (colonIndex < 0)
            return false;

        var condition = expression[..questionIndex].Trim();
        var whenTrue = expression[(questionIndex + 1)..colonIndex].Trim();
        var whenFalse = expression[(colonIndex + 1)..].Trim();
        if (condition.Length == 0 || whenTrue.Length == 0 || whenFalse.Length == 0)
            return false;

        ternary = new TernaryExpressionParts(condition, whenTrue, whenFalse);
        return true;
    }

    private static int FindTopLevelTernaryQuestion(string expression)
    {
        var quote = '\0';
        var escaped = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            var current = expression[i];
            if (UpdateQuotedState(current, ref quote, ref escaped))
                continue;

            UpdateNestingDepth(current, ref parenDepth, ref bracketDepth, ref braceDepth);
            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && current == '?'
                && !IsOptionalOrNullishQuestion(expression, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMatchingTernaryColon(string expression, int startIndex)
    {
        var quote = '\0';
        var escaped = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var nestedTernaryDepth = 0;

        for (var i = startIndex; i < expression.Length; i++)
        {
            var current = expression[i];
            if (UpdateQuotedState(current, ref quote, ref escaped))
                continue;

            UpdateNestingDepth(current, ref parenDepth, ref bracketDepth, ref braceDepth);
            if (parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            if (current == '?' && !IsOptionalOrNullishQuestion(expression, i))
            {
                nestedTernaryDepth++;
                continue;
            }

            if (current != ':')
                continue;

            if (nestedTernaryDepth == 0)
                return i;

            nestedTernaryDepth--;
        }

        return -1;
    }

    private static bool TryInferStringConcatenation(
        string expression,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables,
        IReadOnlySet<string>? nonNullReferences,
        out FlowTypeDescriptor? type)
    {
        type = null;
        var parts = SplitTopLevelPlus(expression);
        if (parts.Count <= 1)
            return false;

        var hasStringOperand = false;
        var allKnownNumeric = true;
        foreach (var part in parts)
        {
            if (part.Length == 0)
                return false;

            var partType = InferExpressionType(part, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
            hasStringOperand |= IsStringLiteral(part) || partType?.Kind == FlowTypeKind.String;
            allKnownNumeric &= partType is { Kind: FlowTypeKind.Integer or FlowTypeKind.Number };
        }

        if (hasStringOperand)
        {
            type = FlowTypeDescriptor.String;
            return true;
        }

        if (allKnownNumeric)
        {
            type = FlowTypeDescriptor.Number;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> SplitTopLevelPlus(string expression)
    {
        var parts = new List<string>();
        var quote = '\0';
        var escaped = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var start = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            var current = expression[i];
            if (UpdateQuotedState(current, ref quote, ref escaped))
                continue;

            UpdateNestingDepth(current, ref parenDepth, ref bracketDepth, ref braceDepth);
            if (parenDepth != 0 || bracketDepth != 0 || braceDepth != 0 || current != '+')
                continue;

            parts.Add(expression[start..i].Trim());
            start = i + 1;
        }

        if (parts.Count == 0)
            return Array.Empty<string>();

        parts.Add(expression[start..].Trim());
        return parts;
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

    private static bool IsStringLiteral(string expression) =>
        (expression.StartsWith('"') && expression.EndsWith('"'))
        || (expression.StartsWith('\'') && expression.EndsWith('\''))
        || expression.StartsWith('`');

    private static bool IsSingleParenthesizedExpression(string value)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (UpdateQuotedState(value[i], ref quote, ref escaped))
                continue;

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
        return ContainsTopLevelBooleanOperator(expression);
    }

    private static bool ContainsTopLevelBooleanOperator(string expression)
    {
        var quote = '\0';
        var escaped = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            var current = expression[i];
            if (UpdateQuotedState(current, ref quote, ref escaped))
                continue;

            UpdateNestingDepth(current, ref parenDepth, ref bracketDepth, ref braceDepth);
            if (parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            if (IsAt(expression, i, "===")
                || IsAt(expression, i, "!==")
                || IsAt(expression, i, "==")
                || IsAt(expression, i, "!=")
                || IsAt(expression, i, ">=")
                || IsAt(expression, i, "<=")
                || IsAt(expression, i, "&&")
                || IsAt(expression, i, "||")
                || current is '>' or '<')
            {
                return true;
            }
        }

        return false;
    }

    private static bool UpdateQuotedState(char current, ref char quote, ref bool escaped)
    {
        if (quote != '\0')
        {
            if (escaped)
            {
                escaped = false;
                return true;
            }

            if (current == '\\')
            {
                escaped = true;
                return true;
            }

            if (current == quote)
                quote = '\0';

            return true;
        }

        if (current is '"' or '\'' or '`')
        {
            quote = current;
            return true;
        }

        return false;
    }

    private static void UpdateNestingDepth(char current, ref int parenDepth, ref int bracketDepth, ref int braceDepth)
    {
        switch (current)
        {
            case '(':
                parenDepth++;
                break;
            case ')':
                parenDepth = Math.Max(0, parenDepth - 1);
                break;
            case '[':
                bracketDepth++;
                break;
            case ']':
                bracketDepth = Math.Max(0, bracketDepth - 1);
                break;
            case '{':
                braceDepth++;
                break;
            case '}':
                braceDepth = Math.Max(0, braceDepth - 1);
                break;
        }
    }

    private static bool IsOptionalOrNullishQuestion(string expression, int index) =>
        index + 1 < expression.Length && expression[index + 1] is '.' or '?';

    private static bool IsAt(string expression, int index, string value) =>
        expression.AsSpan(index).StartsWith(value.AsSpan(), StringComparison.Ordinal);

    private static IReadOnlyList<string> SplitPath(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static class EmptyStringSet
    {
        public static readonly IReadOnlySet<string> Value = new HashSet<string>(StringComparer.Ordinal);
    }
}
