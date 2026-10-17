using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

internal static partial class StepExpressionTypeValidator
{
    // Keep the authoritative schema here: a descriptor union alone loses sibling
    // assertions and cannot choose an object alternative from the supplied request.
    public static IReadOnlyList<StepExpressionTypeMismatch> ValidateInput(
        JsonNode? input, JsonObject inputSchema,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? workflowInputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor> knownStepOutputs,
        IReadOnlyDictionary<string, FlowTypeDescriptor>? dataVariables,
        IReadOnlySet<string>? nonNullReferences)
    {
        // Preserve the established simple-contract path and its diagnostics.
        if (!HasCompositeContract(inputSchema))
            return ValidateInput(input, FlowTypeDescriptorConverter.FromJsonSchema(inputSchema),
                workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
        var errors = new List<StepExpressionTypeMismatch>();
        Check(input, Infer(input), inputSchema, "input", null, errors, new(StringComparer.Ordinal));
        return errors.Distinct().ToArray();

        FlowTypeDescriptor Infer(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Contains("${", StringComparison.Ordinal)
            ? InferInterpolatedStringType(text, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences) ?? FlowTypeDescriptor.Any
            : InferValueType(value, workflowInputs, knownStepOutputs, dataVariables) ?? FlowTypeDescriptor.Any;

        bool Check(JsonNode? value, FlowTypeDescriptor actual, JsonNode? contract, string path, string? expression,
            List<StepExpressionTypeMismatch> findings, HashSet<string> references)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Contains("${", StringComparison.Ordinal))
            {
                expression = text;
                var semantic = ValidateInterpolatedExpressionSemantics(text, path, workflowInputs, knownStepOutputs, dataVariables, nonNullReferences);
                if (semantic is not null) findings.Add(semantic);
            }
            if (contract is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed))
                return allowed || Reject("no allowed value");
            if (contract is not JsonObject schema) return true;
            // Cheap literal discriminators prune an incompatible branch before
            // visiting its recursive children, regardless of property order.
            if (expression is null && value is JsonObject suppliedObject && schema["properties"] is JsonObject declared)
                foreach (var (name, child) in declared)
                    if (child is JsonObject childSchema && suppliedObject.TryGetPropertyValue(name, out var literal) &&
                        literal is not (JsonObject or JsonArray) && !ContainsExpression(literal) &&
                        !JsonSchemaInstanceValidator.MatchesSchema(literal, childSchema, inputSchema)) return false;

            // Every possible source type must be accepted. Do not select a target
            // branch from the first source union member or from an unknown value.
            if (actual.Kind == FlowTypeKind.Union)
            {
                var possible = true;
                foreach (var alternative in actual.Variants)
                    possible &= Check(value, alternative, contract, path, expression, findings, references);
                return possible;
            }
            if (expression is not null && actual.EnumValues.Count > 1)
            {
                var possible = true;
                foreach (var member in actual.EnumValues)
                    possible &= Check(value, actual with { EnumValues = [member] }, contract, path, expression, findings, references);
                return possible;
            }

            var valid = true;
            if (schema["$ref"] is JsonValue referenceValue && referenceValue.TryGetValue<string>(out var reference))
            {
                var key = reference + "|" + path;
                if (references.Add(key))
                {
                    if (JsonSchemaInstanceValidator.TryResolveLocalReference(inputSchema, reference, out var resolved))
                        valid &= Check(value, actual, resolved, path, expression, findings, references);
                    references.Remove(key);
                }
            }
            if (schema["allOf"] is JsonArray all)
                foreach (var part in all) valid &= Check(value, actual, part, path, expression, findings, references);

            // Check common assertions independently; never merge properties across
            // alternatives or discard assertions next to oneOf/anyOf.
            if (!actual.IsOpaque && schema.TryGetPropertyValue("type", out var type))
            {
                var expected = FlowTypeDescriptorConverter.FromJsonSchema(new JsonObject { ["type"] = type?.DeepClone() });
                if (!actual.IsCompatibleWith(expected)) valid &= Reject(expected.Describe());
            }
            if (expression is null && !ContainsExpression(value))
            {
                // Exact literal membership (including numeric equality/const/enum)
                // is owned by the existing instance validator. Literal diagnostics
                // are still emitted by the caller's schema-validation path.
                if (!JsonSchemaInstanceValidator.MatchesSchema(value, schema, inputSchema)) valid = false;
            }
            else if (!actual.IsOpaque)
            {
                // A singleton authoritative string domain supplies an exact
                // value; unlike an unknown string, it can prove oneOf overlap.
                if (actual.Kind == FlowTypeKind.String && actual.EnumValues.Count == 1 &&
                    !JsonSchemaInstanceValidator.MatchesSchema(JsonValue.Create(actual.EnumValues[0]), schema, inputSchema))
                    valid &= Reject("a value satisfying the complete schema");
                if (schema.ContainsKey("const")) valid &= ConstantCompatible(schema["const"]);
                if (schema["enum"] is JsonArray values && !values.Any(ConstantCompatibleWithoutError))
                    valid &= Reject("an allowed enum value");
            }

            if (actual.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary)
            {
                var properties = schema["properties"] as JsonObject;
                var required = schema["required"] as JsonArray;
                if (required is not null)
                    foreach (var name in required.Select(n => n!.GetValue<string>()))
                        if (!actual.Properties.TryGetValue(name, out var property) || !property.Required)
                            valid &= Reject("required property " + name, path + "." + name);
                foreach (var (name, property) in actual.Properties)
                {
                    var supplied = expression is null && value is JsonObject;
                    var childValue = supplied ? value![name] : null;
                    var childType = supplied ? Infer(childValue) : property.Type;
                    if (properties?.TryGetPropertyValue(name, out var child) == true)
                        valid &= Check(childValue, childType, child, path + "." + name, expression, findings, references);
                    else if (schema["additionalProperties"] is { } additional)
                        valid &= Check(childValue, childType, additional, path + "." + name, expression, findings, references);
                }
            }
            if (actual.Kind == FlowTypeKind.Array)
            {
                if (expression is null && value is JsonArray array)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        var child = schema["prefixItems"] is JsonArray prefix && i < prefix.Count ? prefix[i] : schema["items"];
                        if (child is not null) valid &= Check(array[i], Infer(array[i]), child, path + "[" + i + "]", null, findings, references);
                    }
                }
                else if (schema["items"] is { } items && actual.Items is { } itemType)
                    valid &= Check(null, itemType, items, path + "[]", expression, findings, references);
            }

            foreach (var keyword in new[] { "anyOf", "oneOf" })
                if (schema[keyword] is JsonArray alternatives)
                {
                    var matches = 0;
                    var failures = new List<StepExpressionTypeMismatch>();
                    foreach (var alternative in alternatives)
                    {
                        var branch = new List<StepExpressionTypeMismatch>();
                        if (Check(value, actual, alternative, path, expression, branch, references)) matches++;
                        else failures.AddRange(branch);
                    }
                    if (matches == 0)
                    {
                        valid = false;
                        // Deterministic aggregate: no arbitrary first-branch error.
                        foreach (var group in failures.GroupBy(f => (f.Field, f.Expression, f.ActualType)).OrderBy(g => g.Key.Field, StringComparer.Ordinal))
                        {
                            var expected = string.Join(" or ", group.Select(f => f.ExpectedType).Distinct().Order(StringComparer.Ordinal));
                            findings.Add(new(group.Key.Field, group.Key.Expression, expected, group.Key.ActualType,
                                $"Expression assigned to '{group.Key.Field}' resolves to {group.Key.ActualType}, but no schema alternative accepts it (expected {expected})."));
                        }
                    }
                    // With unresolved values, compatibility is not proof of exact
                    // membership. Runtime still counts all oneOf matches.
                    else if (keyword == "oneOf" && expression is null && !ContainsExpression(value) && matches != 1) valid = false;
                }
            return valid;

            bool ConstantCompatibleWithoutError(JsonNode? constant)
            {
                var type = InferValueType(constant, null, new Dictionary<string, FlowTypeDescriptor>())!;
                if (!actual.IsCompatibleWith(type)) return false;
                return actual.EnumValues.Count == 0 || constant is JsonValue v && v.TryGetValue<string>(out var member) && actual.EnumValues.Contains(member, StringComparer.Ordinal);
            }
            bool ConstantCompatible(JsonNode? constant) => ConstantCompatibleWithoutError(constant) || Reject("declared constant");
            bool Reject(string expected, string? field = null)
            {
                if (expression is not null)
                    findings.Add(new(field ?? path, expression, expected, actual.Describe(),
                        $"Expression assigned to '{field ?? path}' resolves to {actual.Describe()}, but the contract requires {expected}."));
                return false;
            }
        }
    }

    private static bool HasCompositeContract(JsonNode? node) => node switch
    {
        JsonObject obj => obj.ContainsKey("anyOf") || obj.ContainsKey("oneOf") || obj.ContainsKey("allOf") || obj.ContainsKey("$ref") || obj.Any(p => HasCompositeContract(p.Value)),
        JsonArray array => array.Any(HasCompositeContract),
        _ => false
    };

    internal static bool ContainsExpression(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p => ContainsExpression(p.Value)),
        JsonArray array => array.Any(ContainsExpression),
        JsonValue value => value.TryGetValue<string>(out var text) && text.Contains("${", StringComparison.Ordinal),
        _ => false
    };
}
