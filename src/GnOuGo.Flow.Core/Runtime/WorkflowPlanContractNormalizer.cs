using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Core.Runtime;

internal static class WorkflowPlanContractNormalizer
{
    public const string WeakOutputSchemaCode = "WEAK_OUTPUT_SCHEMA";

    public static YamlMappingNode? BuildWorkflowOutputFromSchema(JsonNode? schema, string expr)
    {
        var descriptor = FlowTypeDescriptorConverter.FromJsonSchema(schema);
        return BuildWorkflowOutputFromDescriptor(descriptor, expr);
    }

    public static YamlMappingNode? BuildWorkflowOutputFromDescriptor(FlowTypeDescriptor descriptor, string expr)
    {
        var output = BuildCanonicalSchemaYaml(descriptor) as YamlMappingNode;
        if (output == null || IsWeakYamlOutputSchema(output))
            return null;

        output.Children.Remove(Scalar("expr"));
        var withExpr = new YamlMappingNode();
        AddYaml(withExpr, "expr", Scalar(expr));
        foreach (var (key, value) in output.Children)
            withExpr.Add(CloneYamlNode(key), CloneYamlNode(value));
        return withExpr;
    }

    public static YamlMappingNode? BuildSkillOutputFromWorkflowOutputYaml(YamlNode workflowOutputYaml)
    {
        if (workflowOutputYaml is not YamlMappingNode workflowOutput)
            return null;

        var clone = CloneYamlMappingNode(workflowOutput);
        clone.Children.Remove(Scalar("expr"));
        return ContainsYamlKey(clone, "type") && !IsWeakYamlOutputSchema(clone)
            ? clone
            : null;
    }

    public static YamlNode BuildCanonicalSchemaYaml(JsonNode? schema)
        => BuildCanonicalSchemaYaml(FlowTypeDescriptorConverter.FromJsonSchema(schema));

    public static YamlNode BuildCanonicalSchemaYaml(FlowTypeDescriptor descriptor)
    {
        descriptor = NormalizeForWorkflowContract(descriptor);
        var node = FlowTypeDescriptorConverter.ToWorkflowContractNode(
            descriptor,
            inputStyle: false,
            allowScalarShortForm: false);
        return JsonToYaml(CanonicalizeWorkflowContractNode(node));
    }

    public static bool IsWeakYamlOutputSchema(YamlNode node)
        => IsWeakDescriptor(FlowTypeDescriptorConverter.FromJsonSchema(WorkflowParserYamlToJson(node)));

    public static bool IsWeakOutputDef(OutputDef output, bool allowSkillScalarTypeShorthand = false)
        => IsWeakDescriptor(OutputDefToDescriptor(output, allowSkillScalarTypeShorthand));

    public static bool PruneWeakNestedOutputProperties(YamlNode outputSchema)
    {
        if (outputSchema is not YamlMappingNode mapping)
            return false;

        var changed = false;
        if (mapping.Children.TryGetValue(Scalar("properties"), out var propertiesNode)
            && propertiesNode is YamlMappingNode properties)
        {
            foreach (var (propertyKey, originalPropertySchema) in properties.Children.ToArray())
            {
                var propertySchema = CanonicalizeRepresentableUnion(originalPropertySchema, out var canonicalized);
                if (canonicalized)
                {
                    properties.Children[propertyKey] = propertySchema;
                    changed = true;
                }
                changed |= PruneWeakNestedOutputProperties(propertySchema);
                if (!IsWeakYamlOutputSchema(propertySchema))
                    continue;

                properties.Children.Remove(propertyKey);
                RemoveRequiredProperty(mapping, propertyKey);
                changed = true;
            }
        }

        if (mapping.Children.TryGetValue(Scalar("items"), out var items))
        {
            var normalizedItems = CanonicalizeRepresentableUnion(items, out var canonicalized);
            if (canonicalized)
            {
                mapping.Children[Scalar("items")] = normalizedItems;
                changed = true;
            }
            changed |= PruneWeakNestedOutputProperties(normalizedItems);
        }
        if (mapping.Children.TryGetValue(Scalar("additional_properties"), out var additionalProperties))
        {
            var normalizedAdditional = CanonicalizeRepresentableUnion(additionalProperties, out var canonicalized);
            if (canonicalized)
            {
                mapping.Children[Scalar("additional_properties")] = normalizedAdditional;
                changed = true;
            }
            changed |= PruneWeakNestedOutputProperties(normalizedAdditional);
        }
        if (mapping.Children.TryGetValue(Scalar("additionalProperties"), out var jsonAdditionalProperties))
        {
            var normalizedAdditional = CanonicalizeRepresentableUnion(jsonAdditionalProperties, out var canonicalized);
            if (canonicalized)
            {
                mapping.Children[Scalar("additionalProperties")] = normalizedAdditional;
                changed = true;
            }
            changed |= PruneWeakNestedOutputProperties(normalizedAdditional);
        }

        return changed;
    }

    /// <summary>
    /// Normalizes workflow-contract shorthand that a planner may place inside a
    /// set step's JSON Schema. Public workflow contracts accept names such as
    /// <c>dictionary</c>, <c>required_properties</c>, and
    /// <c>additional_properties</c>; JSON Schema does not.
    /// </summary>
    public static bool NormalizeSetOutputSchema(YamlNode outputSchema)
    {
        if (outputSchema is not YamlMappingNode)
            return false;

        // Unlike public workflow output contracts, set output schemas use JSON
        // Schema directly and may precisely represent nullable unions. Do not
        // prune those fields or narrow their runtime contract here.
        _ = NormalizeJsonSchemaNode(outputSchema, out var changed);
        return changed;
    }

    private static YamlNode NormalizeJsonSchemaNode(YamlNode schema, out bool changed)
    {
        changed = false;
        if (schema is YamlScalarNode scalar)
        {
            var type = scalar.Value?.Trim().ToLowerInvariant();
            if (type is not ("string" or "number" or "integer" or "boolean" or "null"
                or "object" or "array" or "dictionary"))
            {
                return schema;
            }

            changed = true;
            return new YamlMappingNode
            {
                { Scalar("type"), Scalar(type == "dictionary" ? "object" : type) }
            };
        }

        if (schema is not YamlMappingNode mapping)
            return schema;

        changed |= RenameJsonSchemaKeyword(mapping, "required_properties", "required");
        changed |= RenameJsonSchemaKeyword(mapping, "additional_properties", "additionalProperties");

        if (mapping.Children.TryGetValue(Scalar("type"), out var typeNode))
        {
            if (typeNode is YamlScalarNode typeScalar
                && string.Equals(typeScalar.Value, "dictionary", StringComparison.OrdinalIgnoreCase))
            {
                mapping.Children[Scalar("type")] = Scalar("object");
                changed = true;
            }
            else if (typeNode is YamlSequenceNode typeSequence)
            {
                for (var index = 0; index < typeSequence.Children.Count; index++)
                {
                    if (typeSequence.Children[index] is not YamlScalarNode candidate
                        || !string.Equals(candidate.Value, "dictionary", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    typeSequence.Children[index] = Scalar("object");
                    changed = true;
                }
            }
        }

        if (mapping.Children.TryGetValue(Scalar("properties"), out var propertiesNode)
            && propertiesNode is YamlMappingNode properties)
        {
            foreach (var property in properties.Children.ToArray())
            {
                var normalized = NormalizeJsonSchemaNode(property.Value, out var childChanged);
                if (childChanged)
                {
                    properties.Children[property.Key] = normalized;
                    changed = true;
                }
            }
        }

        foreach (var keyword in new[] { "items", "additionalProperties" })
        {
            var key = Scalar(keyword);
            if (!mapping.Children.TryGetValue(key, out var child))
                continue;
            var normalized = NormalizeJsonSchemaNode(child, out var childChanged);
            if (childChanged)
            {
                mapping.Children[key] = normalized;
                changed = true;
            }
        }

        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (!mapping.Children.TryGetValue(Scalar(keyword), out var variantsNode)
                || variantsNode is not YamlSequenceNode variants)
            {
                continue;
            }

            for (var index = 0; index < variants.Children.Count; index++)
            {
                var normalized = NormalizeJsonSchemaNode(variants.Children[index], out var childChanged);
                if (childChanged)
                {
                    variants.Children[index] = normalized;
                    changed = true;
                }
            }
        }

        foreach (var definitionsKeyword in new[] { "$defs", "definitions" })
        {
            if (!mapping.Children.TryGetValue(Scalar(definitionsKeyword), out var definitionsNode)
                || definitionsNode is not YamlMappingNode definitions)
            {
                continue;
            }

            foreach (var definition in definitions.Children.ToArray())
            {
                var normalized = NormalizeJsonSchemaNode(definition.Value, out var childChanged);
                if (childChanged)
                {
                    definitions.Children[definition.Key] = normalized;
                    changed = true;
                }
            }
        }

        return mapping;
    }

    private static bool RenameJsonSchemaKeyword(YamlMappingNode mapping, string workflowName, string jsonName)
    {
        var workflowKey = Scalar(workflowName);
        if (!mapping.Children.TryGetValue(workflowKey, out var value))
            return false;

        var jsonKey = Scalar(jsonName);
        if (!mapping.Children.ContainsKey(jsonKey))
            mapping.Children[jsonKey] = value;
        mapping.Children.Remove(workflowKey);
        return true;
    }

    private static bool ContainsNullUnionVariant(YamlNode schema)
    {
        if (schema is not YamlMappingNode mapping)
            return false;

        if (mapping.Children.TryGetValue(Scalar("type"), out var typeNode)
            && typeNode is YamlSequenceNode types
            && types.Children.OfType<YamlScalarNode>().Any(static type =>
                string.Equals(type.Value, "null", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (!mapping.Children.TryGetValue(Scalar(key), out var variantsNode)
                || variantsNode is not YamlSequenceNode variants)
            {
                continue;
            }

            if (variants.Children.Any(static variant => variant is YamlScalarNode scalar
                    && string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase)
                || variant is YamlMappingNode variantMapping
                && variantMapping.Children.TryGetValue(Scalar("type"), out var variantType)
                && variantType is YamlScalarNode variantTypeScalar
                && string.Equals(variantTypeScalar.Value, "null", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static YamlNode CanonicalizeRepresentableUnion(YamlNode schema, out bool changed)
    {
        changed = false;
        if (schema is not YamlMappingNode mapping)
            return schema;
        var hasUnion = mapping.Children.ContainsKey(Scalar("anyOf"))
                       || mapping.Children.ContainsKey(Scalar("oneOf"))
                       || mapping.Children.TryGetValue(Scalar("type"), out var typeNode)
                       && typeNode is YamlSequenceNode;
        if (!hasUnion)
            return schema;

        var canonical = BuildCanonicalSchemaYaml(WorkflowParserYamlToJson(schema));
        if (IsWeakYamlOutputSchema(canonical))
            return schema;

        changed = true;
        return canonical;
    }

    private static void RemoveRequiredProperty(YamlMappingNode schema, YamlNode propertyKey)
    {
        foreach (var key in new[] { "required_properties", "required" })
        {
            if (!schema.Children.TryGetValue(Scalar(key), out var requiredNode)
                || requiredNode is not YamlSequenceNode required)
            {
                continue;
            }

            var propertyName = (propertyKey as YamlScalarNode)?.Value;
            for (var index = required.Children.Count - 1; index >= 0; index--)
            {
                if (required.Children[index] is YamlScalarNode requiredName
                    && string.Equals(requiredName.Value, propertyName, StringComparison.Ordinal))
                {
                    required.Children.RemoveAt(index);
                }
            }
        }
    }

    public static bool IsWeakDescriptor(FlowTypeDescriptor descriptor)
    {
        descriptor = NormalizeForWorkflowContract(descriptor.RemoveNull());
        if (descriptor.IsOpaque)
            return true;

        return descriptor.Kind switch
        {
            FlowTypeKind.Array => descriptor.Items == null || IsWeakDescriptor(descriptor.Items),
            FlowTypeKind.Object => descriptor.Properties.Count == 0
                                   && (descriptor.AdditionalProperties == null
                                       || IsWeakDescriptor(descriptor.AdditionalProperties)),
            FlowTypeKind.Dictionary => descriptor.AdditionalProperties == null || IsWeakDescriptor(descriptor.AdditionalProperties),
            FlowTypeKind.Union => descriptor.Variants.Count == 0 || descriptor.Variants.Any(IsWeakDescriptor),
            _ => false
        };
    }

    private static FlowTypeDescriptor NormalizeForWorkflowContract(FlowTypeDescriptor descriptor)
    {
        if (descriptor.Kind == FlowTypeKind.Union)
        {
            var containsNull = descriptor.Variants.Any(static variant => variant.Kind == FlowTypeKind.Null);
            var variants = descriptor.Variants
                .Where(static variant => variant.Kind != FlowTypeKind.Null)
                .Select(NormalizeForWorkflowContract)
                .ToArray();

            if (variants.Length == 0)
                return FlowTypeDescriptor.Any;
            FlowTypeDescriptor normalized = variants.Length == 1
                ? variants[0]
                : variants.All(static variant => variant.Kind == FlowTypeKind.Array)
                    ? FlowTypeDescriptor.Array(FlowTypeDescriptor.Union(variants.Select(static variant => variant.Items ?? FlowTypeDescriptor.Any)))
                    : variants.All(static variant => variant.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary)
                        ? MergeObjectLikeVariants(variants)
                        : variants.Select(static variant => variant.Kind).Distinct().Count() == 1
                            ? variants[0]
                            : FlowTypeDescriptor.Any;
            return containsNull && !normalized.IsOpaque
                ? FlowTypeDescriptor.Union([normalized, FlowTypeDescriptor.Null]) with
                {
                    Description = descriptor.Description,
                    Default = descriptor.Default
                }
                : normalized;
        }

        if (descriptor.Kind == FlowTypeKind.Array)
            return descriptor with { Items = descriptor.Items == null ? null : NormalizeForWorkflowContract(descriptor.Items) };

        if (descriptor.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary)
        {
            return descriptor with
            {
                Properties = descriptor.Properties.ToDictionary(
                    static pair => pair.Key,
                    pair => new FlowPropertyDescriptor(NormalizeForWorkflowContract(pair.Value.Type), pair.Value.Required),
                    StringComparer.Ordinal),
                AdditionalProperties = descriptor.AdditionalProperties == null
                    ? null
                    : NormalizeForWorkflowContract(descriptor.AdditionalProperties)
            };
        }

        return descriptor;
    }

    /// <summary>
    /// Workflow output contracts cannot express nullable unions. Preserve the
    /// sound part of an object contract by omitting nullable properties, but do
    /// not narrow a nullable root or array item to its non-null variant. Returning
    /// <see cref="FlowTypeDescriptor.Any"/> for those positions makes the
    /// resulting public contract weak so generation fails closed instead of
    /// accepting a contract that can reject valid runtime values.
    /// </summary>
    private static FlowTypeDescriptor RemoveUnrepresentableNullableValues(FlowTypeDescriptor descriptor)
    {
        if (ContainsNullVariant(descriptor))
            return FlowTypeDescriptor.Any;

        if (descriptor.Kind == FlowTypeKind.Array)
        {
            return descriptor with
            {
                Items = descriptor.Items == null
                    ? null
                    : RemoveUnrepresentableNullableValues(descriptor.Items)
            };
        }

        if (descriptor.Kind is not (FlowTypeKind.Object or FlowTypeKind.Dictionary))
            return descriptor;

        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        foreach (var (name, property) in descriptor.Properties)
        {
            if (ContainsNullVariant(property.Type))
                continue;

            var normalized = RemoveUnrepresentableNullableValues(property.Type);
            if (normalized.IsOpaque)
                continue;

            properties[name] = new FlowPropertyDescriptor(normalized, property.Required);
        }

        var additionalProperties = descriptor.AdditionalProperties == null
            || ContainsNullVariant(descriptor.AdditionalProperties)
            ? null
            : RemoveUnrepresentableNullableValues(descriptor.AdditionalProperties);
        if (additionalProperties?.IsOpaque == true)
            additionalProperties = null;

        return descriptor with
        {
            Properties = properties,
            AdditionalProperties = additionalProperties
        };
    }

    private static bool ContainsNullVariant(FlowTypeDescriptor descriptor)
        => descriptor.Kind == FlowTypeKind.Null
           || descriptor.Kind == FlowTypeKind.Union
           && descriptor.Variants.Any(static variant => variant.Kind == FlowTypeKind.Null);

    private static FlowTypeDescriptor MergeObjectLikeVariants(IReadOnlyList<FlowTypeDescriptor> variants)
    {
        var propertyNames = variants
            .SelectMany(static variant => variant.Properties.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        foreach (var name in propertyNames)
        {
            var propertyVariants = variants
                .Select(variant => variant.Properties.TryGetValue(name, out var property) ? property : null)
                .Where(static property => property != null)
                .Cast<FlowPropertyDescriptor>()
                .ToArray();
            if (propertyVariants.Length == 0)
                continue;

            properties[name] = new FlowPropertyDescriptor(
                FlowTypeDescriptor.Union(propertyVariants.Select(static property => property.Type)),
                Required: propertyVariants.Length == variants.Count && propertyVariants.All(static property => property.Required));
        }

        return FlowTypeDescriptor.Object(properties);
    }

    public static void CollectWeakOutputSchemaDiagnostics(
        OutputDef output,
        string path,
        JsonArray diagnostics,
        bool allowSkillScalarTypeShorthand)
    {
        var descriptor = OutputDefToDescriptor(output, allowSkillScalarTypeShorthand);
        CollectWeakDescriptorDiagnostics(descriptor, path, diagnostics);
    }

    public static JsonObject BuildWeakOutputSchemaDiagnostic(string path, string message, string expected)
        => new()
        {
            ["code"] = WeakOutputSchemaCode,
            ["phase"] = "output_schema_validation",
            ["location"] = path,
            ["message"] = message,
            ["expected"] = expected,
            ["hint"] = "Generated workflow outputs are public contracts and must be concrete.",
            ["llm_guidance"] = "Use the exact output path and add a concrete schema. Arrays need items; object outputs and object array items need properties; do not use any."
        };

    public static bool ContainsYamlKey(YamlMappingNode node, string key)
        => node.Children.ContainsKey(Scalar(key));

    public static void ReplaceYaml(YamlMappingNode node, string key, YamlNode value)
    {
        node.Children.Remove(Scalar(key));
        AddYaml(node, key, value);
    }

    public static void AddYaml(YamlMappingNode node, string key, YamlNode value)
        => node.Children.Add(Scalar(key), CloneYamlNode(value));

    public static YamlNode JsonToYaml(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => JsonObjectToYaml(obj),
            JsonArray array => JsonArrayToYaml(array),
            JsonValue value when value.TryGetValue<string>(out var s) => JsonStringScalar(s),
            JsonValue value when value.TryGetValue<bool>(out var b) => Scalar(b ? "true" : "false"),
            JsonValue value when value.TryGetValue<int>(out var i) => Scalar(i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonValue value when value.TryGetValue<long>(out var l) => Scalar(l.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonValue value when value.TryGetValue<double>(out var d) => Scalar(d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonValue value when value.TryGetValue<decimal>(out var m) => Scalar(m.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            null => Scalar(""),
            _ => Scalar(node.ToJsonString())
        };
    }

    private static YamlScalarNode JsonStringScalar(string value)
    {
        var scalar = Scalar(value);
        if (value is "null" or "~" or "true" or "True" or "false" or "False"
            || int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _)
            || double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            // Keep JSON strings as strings across the YAML round trip. In particular,
            // JSON Schema's `type: "null"` must not become a YAML null value.
            scalar.Style = ScalarStyle.DoubleQuoted;
        }
        return scalar;
    }

    public static YamlNode CloneYamlNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return new YamlScalarNode(scalar.Value)
                {
                    Style = scalar.Style
                };

            case YamlSequenceNode sequence:
            {
                var clone = new YamlSequenceNode
                {
                    Style = sequence.Style
                };
                foreach (var child in sequence.Children)
                    clone.Add(CloneYamlNode(child));
                return clone;
            }

            case YamlMappingNode mapping:
            {
                var clone = new YamlMappingNode
                {
                    Style = mapping.Style
                };
                foreach (var (key, value) in mapping.Children)
                    clone.Add(CloneYamlNode(key), CloneYamlNode(value));
                return clone;
            }

            default:
                return node;
        }
    }

    public static YamlMappingNode CloneYamlMappingNode(YamlMappingNode node)
        => (YamlMappingNode)CloneYamlNode(node);

    public static YamlScalarNode Scalar(string value) => new(value);

    private static void CollectWeakDescriptorDiagnostics(
        FlowTypeDescriptor descriptor,
        string path,
        JsonArray diagnostics)
    {
        descriptor = descriptor.RemoveNull();
        switch (descriptor.Kind)
        {
            case FlowTypeKind.Any:
                diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                    path,
                    "Output schema uses type any.",
                    "concrete scalar, object, array, or dictionary schema"));
                break;

            case FlowTypeKind.Array:
                if (descriptor.Items == null || descriptor.Items.IsOpaque)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Array output schema does not declare items.",
                        "array with concrete items schema"));
                }
                else
                {
                    CollectWeakDescriptorDiagnostics(descriptor.Items, path + ".items", diagnostics);
                }
                break;

            case FlowTypeKind.Object:
                if (descriptor.Properties.Count == 0)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Object output schema does not declare properties.",
                        "object with non-empty properties"));
                }
                foreach (var (name, property) in descriptor.Properties)
                    CollectWeakDescriptorDiagnostics(property.Type, $"{path}.properties.{name}", diagnostics);
                break;

            case FlowTypeKind.Dictionary:
                if (descriptor.AdditionalProperties == null)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Dictionary output schema does not declare additional_properties.",
                        "dictionary with concrete additional_properties schema"));
                }
                else
                {
                    CollectWeakDescriptorDiagnostics(descriptor.AdditionalProperties, path + ".additional_properties", diagnostics);
                }
                break;

            case FlowTypeKind.Union:
                if (descriptor.Variants.Count == 0)
                {
                    diagnostics.Add((JsonNode)BuildWeakOutputSchemaDiagnostic(
                        path,
                        "Output schema uses type any.",
                        "concrete scalar, object, array, or dictionary schema"));
                    break;
                }
                foreach (var variant in descriptor.Variants)
                    CollectWeakDescriptorDiagnostics(variant, path, diagnostics);
                break;
        }
    }

    private static FlowTypeDescriptor OutputDefToDescriptor(OutputDef output, bool allowSkillScalarTypeShorthand)
    {
        var descriptor = FlowTypeDescriptorConverter.FromOutputDef(output);
        if (!allowSkillScalarTypeShorthand || !descriptor.IsOpaque)
            return descriptor;

        return NormalizeType(output.Expr) switch
        {
            "string" => FlowTypeDescriptor.String,
            "number" => FlowTypeDescriptor.Number,
            "integer" => FlowTypeDescriptor.Integer,
            "boolean" => FlowTypeDescriptor.Boolean,
            "array" => FlowTypeDescriptor.Array(),
            "object" => FlowTypeDescriptor.Object(),
            "dictionary" => FlowTypeDescriptor.Dictionary(),
            _ => descriptor
        };
    }

    private static string NormalizeType(string? type) => type?.ToLowerInvariant() switch
    {
        "string" => "string",
        "number" => "number",
        "integer" => "integer",
        "boolean" or "bool" => "boolean",
        "array" => "array",
        "object" => "object",
        "dictionary" => "dictionary",
        "null" => "null",
        "any" => "any",
        _ => "any"
    };

    private static JsonNode? WorkflowParserYamlToJson(YamlNode node)
        => GnOuGo.Flow.Core.Parsing.WorkflowParser.YamlToJson(node);

    private static JsonNode? CanonicalizeWorkflowContractNode(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var scalarType))
            return new JsonObject { ["type"] = scalarType };

        if (node is JsonArray array)
        {
            return new JsonArray(array
                .Select(CanonicalizeWorkflowContractNode)
                .ToArray());
        }

        if (node is not JsonObject obj)
            return node?.DeepClone();

        var copy = new JsonObject();
        foreach (var (name, child) in obj)
        {
            if (child is JsonObject properties
                && string.Equals(name, "properties", StringComparison.Ordinal))
            {
                var canonicalProperties = new JsonObject();
                foreach (var (propertyName, propertySchema) in properties)
                    canonicalProperties[propertyName] = CanonicalizeWorkflowContractNode(propertySchema);
                copy[name] = canonicalProperties;
                continue;
            }

            if (string.Equals(name, "items", StringComparison.Ordinal)
                || string.Equals(name, "additionalProperties", StringComparison.Ordinal)
                || string.Equals(name, "additional_properties", StringComparison.Ordinal)
                || string.Equals(name, "any_of", StringComparison.Ordinal)
                || string.Equals(name, "anyOf", StringComparison.Ordinal)
                || string.Equals(name, "oneOf", StringComparison.Ordinal))
            {
                copy[name] = CanonicalizeWorkflowContractNode(child);
                continue;
            }

            copy[name] = child?.DeepClone();
        }

        return copy;
    }

    private static YamlNode JsonObjectToYaml(JsonObject obj)
    {
        var map = new YamlMappingNode();
        foreach (var (key, childNode) in obj)
            AddYaml(map, key, JsonToYaml(childNode));
        return map;
    }

    private static YamlNode JsonArrayToYaml(JsonArray array)
    {
        var sequence = new YamlSequenceNode();
        foreach (var item in array)
            sequence.Add(JsonToYaml(item));
        return sequence;
    }
}
