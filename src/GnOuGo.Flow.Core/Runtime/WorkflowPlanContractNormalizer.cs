using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
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
        descriptor = NormalizeForWorkflowContract(descriptor.RemoveNullDeep());
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

    public static bool IsWeakDescriptor(FlowTypeDescriptor descriptor)
    {
        descriptor = NormalizeForWorkflowContract(descriptor.RemoveNull());
        if (descriptor.IsOpaque)
            return true;

        return descriptor.Kind switch
        {
            FlowTypeKind.Array => descriptor.Items == null || IsWeakDescriptor(descriptor.Items),
            FlowTypeKind.Object => descriptor.Properties.Count == 0,
            FlowTypeKind.Dictionary => descriptor.AdditionalProperties == null || IsWeakDescriptor(descriptor.AdditionalProperties),
            FlowTypeKind.Union => descriptor.Variants.Count == 0 || descriptor.Variants.Any(IsWeakDescriptor),
            _ => false
        };
    }

    private static FlowTypeDescriptor NormalizeForWorkflowContract(FlowTypeDescriptor descriptor)
    {
        if (descriptor.Kind == FlowTypeKind.Union)
        {
            var variants = descriptor.Variants
                .Where(static variant => variant.Kind != FlowTypeKind.Null)
                .Select(NormalizeForWorkflowContract)
                .ToArray();

            if (variants.Length == 0)
                return FlowTypeDescriptor.Any;
            if (variants.Length == 1)
                return variants[0];
            if (variants.All(static variant => variant.Kind == FlowTypeKind.Array))
                return FlowTypeDescriptor.Array(FlowTypeDescriptor.Union(variants.Select(static variant => variant.Items ?? FlowTypeDescriptor.Any)));
            if (variants.All(static variant => variant.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary))
                return MergeObjectLikeVariants(variants);
            if (variants.Select(static variant => variant.Kind).Distinct().Count() == 1)
                return variants[0];

            return FlowTypeDescriptor.Any;
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
            JsonValue value when value.TryGetValue<string>(out var s) => Scalar(s),
            JsonValue value when value.TryGetValue<bool>(out var b) => Scalar(b ? "true" : "false"),
            JsonValue value when value.TryGetValue<int>(out var i) => Scalar(i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonValue value when value.TryGetValue<long>(out var l) => Scalar(l.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonValue value when value.TryGetValue<double>(out var d) => Scalar(d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonValue value when value.TryGetValue<decimal>(out var m) => Scalar(m.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            null => Scalar(""),
            _ => Scalar(node.ToJsonString())
        };
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
