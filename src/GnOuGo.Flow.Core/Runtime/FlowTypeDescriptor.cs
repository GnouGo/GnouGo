using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal enum FlowTypeKind
{
    Any,
    Null,
    String,
    Number,
    Integer,
    Boolean,
    Array,
    Object,
    Dictionary,
    Union
}

internal sealed record FlowPropertyDescriptor(
    FlowTypeDescriptor Type,
    bool Required = false);

internal sealed record FlowTypeDescriptor
{
    private static readonly IReadOnlyDictionary<string, FlowPropertyDescriptor> EmptyProperties =
        new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<FlowTypeDescriptor> EmptyVariants = System.Array.Empty<FlowTypeDescriptor>();
    private static readonly IReadOnlyList<string> EmptyEnumValues = System.Array.Empty<string>();

    public FlowTypeKind Kind { get; init; }
    public FlowTypeDescriptor? Items { get; init; }
    public IReadOnlyDictionary<string, FlowPropertyDescriptor> Properties { get; init; } = EmptyProperties;
    public FlowTypeDescriptor? AdditionalProperties { get; init; }
    public bool AllowsAdditionalProperties { get; init; } = true;
    public IReadOnlyList<FlowTypeDescriptor> Variants { get; init; } = EmptyVariants;
    public IReadOnlyList<string> EnumValues { get; init; } = EmptyEnumValues;
    public decimal? Minimum { get; init; }
    public string? Description { get; init; }
    public JsonNode? Default { get; init; }

    public bool IsOpaque => Kind == FlowTypeKind.Any;

    public static FlowTypeDescriptor Any { get; } = new() { Kind = FlowTypeKind.Any };
    public static FlowTypeDescriptor Null { get; } = new() { Kind = FlowTypeKind.Null };
    public static FlowTypeDescriptor String { get; } = new() { Kind = FlowTypeKind.String };
    public static FlowTypeDescriptor Number { get; } = new() { Kind = FlowTypeKind.Number };
    public static FlowTypeDescriptor Integer { get; } = new() { Kind = FlowTypeKind.Integer };
    public static FlowTypeDescriptor Boolean { get; } = new() { Kind = FlowTypeKind.Boolean };

    public static FlowTypeDescriptor Array(FlowTypeDescriptor? items = null) =>
        new() { Kind = FlowTypeKind.Array, Items = items ?? Any };

    public static FlowTypeDescriptor Object(
        IReadOnlyDictionary<string, FlowPropertyDescriptor>? properties = null,
        bool allowsAdditionalProperties = false,
        FlowTypeDescriptor? additionalProperties = null) =>
        new()
        {
            Kind = FlowTypeKind.Object,
            Properties = properties ?? EmptyProperties,
            AllowsAdditionalProperties = allowsAdditionalProperties || additionalProperties != null,
            AdditionalProperties = additionalProperties
        };

    public static FlowTypeDescriptor Dictionary(FlowTypeDescriptor? valueType = null) =>
        new()
        {
            Kind = FlowTypeKind.Dictionary,
            AllowsAdditionalProperties = true,
            AdditionalProperties = valueType ?? Any
        };

    public static FlowTypeDescriptor Enum(params string[] values) =>
        String with { EnumValues = values };

    public static FlowTypeDescriptor Union(IEnumerable<FlowTypeDescriptor> variants)
    {
        var flattened = new List<FlowTypeDescriptor>();
        foreach (var variant in variants)
        {
            if (variant.Kind == FlowTypeKind.Union)
                flattened.AddRange(variant.Variants);
            else
                flattened.Add(variant);
        }

        var unique = flattened
            .Where(static variant => variant.Kind != FlowTypeKind.Union || variant.Variants.Count > 0)
            .Distinct(FlowTypeDescriptorStructuralComparer.Instance)
            .ToArray();

        return unique.Length switch
        {
            0 => Any,
            1 => unique[0],
            _ => new FlowTypeDescriptor { Kind = FlowTypeKind.Union, Variants = unique }
        };
    }

    public FlowTypeDescriptor RemoveNull()
    {
        if (Kind == FlowTypeKind.Null)
            return Any;

        if (Kind != FlowTypeKind.Union)
            return this;

        var variants = Variants
            .Where(static variant => variant.Kind != FlowTypeKind.Null)
            .Select(static variant => variant.RemoveNull())
            .ToArray();

        return variants.Length == 0 ? Any : Union(variants);
    }

    public FlowTypeDescriptor RemoveNullDeep()
    {
        if (Kind == FlowTypeKind.Null)
            return Any;

        if (Kind == FlowTypeKind.Union)
        {
            var variants = Variants
                .Where(static variant => variant.Kind != FlowTypeKind.Null)
                .Select(static variant => variant.RemoveNullDeep())
                .ToArray();

            return variants.Length == 0 ? Any : Union(variants);
        }

        if (Kind == FlowTypeKind.Array)
            return this with { Items = Items?.RemoveNullDeep() };

        if (Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary)
        {
            return this with
            {
                Properties = Properties.ToDictionary(
                    static pair => pair.Key,
                    static pair => new FlowPropertyDescriptor(pair.Value.Type.RemoveNullDeep(), pair.Value.Required),
                    StringComparer.Ordinal),
                AdditionalProperties = AdditionalProperties?.RemoveNullDeep()
            };
        }

        return this;
    }

    public FlowTypeDescriptor? ResolvePath(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
            return this;

        if (IsOpaque)
            return null;

        if (Kind == FlowTypeKind.Union)
        {
            var resolved = Variants
                .Select(variant => variant.ResolvePath(path))
                .Where(static variant => variant != null)
                .Cast<FlowTypeDescriptor>()
                .ToArray();

            return resolved.Length == 0 ? null : Union(resolved);
        }

        if (Kind is not (FlowTypeKind.Object or FlowTypeKind.Dictionary))
            return null;

        var segment = path[0];
        FlowTypeDescriptor? child = null;
        if (Properties.TryGetValue(segment, out var property))
            child = property.Type;
        else if (AdditionalProperties != null)
            child = AdditionalProperties;
        else if (AllowsAdditionalProperties)
            child = Any;

        return child == null ? null : child.ResolvePath(path.Skip(1).ToArray());
    }

    public bool IsCompatibleWith(FlowTypeDescriptor expected)
    {
        if (IsOpaque || expected.IsOpaque)
            return true;

        if (Kind == FlowTypeKind.Union)
            return Variants.All(variant => variant.IsCompatibleWith(expected));

        if (expected.Kind == FlowTypeKind.Union)
            return expected.Variants.Any(IsCompatibleWith);

        if (Kind == expected.Kind)
            return true;

        return Kind == FlowTypeKind.Integer && expected.Kind == FlowTypeKind.Number;
    }

    public string Describe()
    {
        var types = EnumerateKindNames()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return types.Length == 0 ? "compatible" : string.Join(" or ", types);
    }

    private IEnumerable<string> EnumerateKindNames()
    {
        if (Kind == FlowTypeKind.Union)
        {
            foreach (var variant in Variants)
            foreach (var name in variant.EnumerateKindNames())
                yield return name;
            yield break;
        }

        yield return Kind switch
        {
            FlowTypeKind.Any => "compatible",
            FlowTypeKind.Null => "null",
            FlowTypeKind.String => "string",
            FlowTypeKind.Number => "number",
            FlowTypeKind.Integer => "integer",
            FlowTypeKind.Boolean => "boolean",
            FlowTypeKind.Array => "array",
            FlowTypeKind.Object => "object",
            FlowTypeKind.Dictionary => "object",
            _ => "value"
        };
    }

    private sealed class FlowTypeDescriptorStructuralComparer : IEqualityComparer<FlowTypeDescriptor>
    {
        public static readonly FlowTypeDescriptorStructuralComparer Instance = new();

        public bool Equals(FlowTypeDescriptor? x, FlowTypeDescriptor? y) =>
            x != null
            && y != null
            && StructuralKey(x) == StructuralKey(y);

        public int GetHashCode(FlowTypeDescriptor obj) =>
            StructuralKey(obj).GetHashCode(StringComparison.Ordinal);

        private static string StructuralKey(FlowTypeDescriptor descriptor)
        {
            var parts = new List<string> { descriptor.Kind.ToString() };
            if (descriptor.Items != null)
                parts.Add("items:" + StructuralKey(descriptor.Items));
            if (descriptor.AdditionalProperties != null)
                parts.Add("additional:" + StructuralKey(descriptor.AdditionalProperties));
            parts.Add("allowsAdditional:" + descriptor.AllowsAdditionalProperties);
            if (descriptor.EnumValues.Count > 0)
                parts.Add("enum:" + string.Join("|", descriptor.EnumValues));
            if (descriptor.Variants.Count > 0)
                parts.Add("variants:" + string.Join(",", descriptor.Variants.Select(StructuralKey).OrderBy(static value => value, StringComparer.Ordinal)));
            if (descriptor.Properties.Count > 0)
            {
                parts.Add("properties:" + string.Join(
                    ",",
                    descriptor.Properties
                        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .Select(static pair => $"{pair.Key}:{pair.Value.Required}:{StructuralKey(pair.Value.Type)}")));
            }

            return string.Join(";", parts);
        }
    }
}

internal static class FlowTypeDescriptorConverter
{
    public static FlowTypeDescriptor FromInputDef(InputDef definition)
    {
        var type = NormalizeWorkflowType(definition.Type);
        var descriptor = FromWorkflowSchemaParts(
            type,
            definition.Items == null ? null : FromInputDef(definition.Items),
            definition.Properties?.ToDictionary(
                static pair => pair.Key,
                pair => new FlowPropertyDescriptor(
                    FromInputDef(pair.Value),
                    definition.RequiredProperties == null
                        ? pair.Value.Required
                        : definition.RequiredProperties.Contains(pair.Key)),
                StringComparer.Ordinal),
            definition.AdditionalProperties == null ? null : FromInputDef(definition.AdditionalProperties));

        return descriptor with
        {
            Description = definition.Description,
            Default = definition.Default == null
                ? null
                : InputDefaultValueConverter.ConvertToNode(definition.Default, definition)
        };
    }

    public static FlowTypeDescriptor FromOutputDef(OutputDef definition)
    {
        var type = NormalizeWorkflowType(definition.Type);
        return FromWorkflowSchemaParts(
            type,
            definition.Items == null ? null : FromOutputDef(definition.Items),
            definition.Properties?.ToDictionary(
                static pair => pair.Key,
                pair => new FlowPropertyDescriptor(
                    FromOutputDef(pair.Value),
                    definition.RequiredProperties?.Contains(pair.Key) == true),
                StringComparer.Ordinal),
            definition.AdditionalProperties == null ? null : FromOutputDef(definition.AdditionalProperties)) with
        {
            Description = definition.Description
        };
    }

    public static FlowTypeDescriptor InputsObject(IReadOnlyDictionary<string, InputDef>? inputs)
    {
        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        if (inputs != null)
        {
            foreach (var (name, definition) in inputs)
                properties[name] = new FlowPropertyDescriptor(FromInputDef(definition), definition.Required);
        }

        return FlowTypeDescriptor.Object(properties);
    }

    public static FlowTypeDescriptor OutputsObject(IReadOnlyDictionary<string, OutputDef>? outputs)
    {
        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        if (outputs != null)
        {
            foreach (var (name, definition) in outputs)
                properties[name] = new FlowPropertyDescriptor(FromOutputDef(definition), Required: true);
        }

        return FlowTypeDescriptor.Object(properties);
    }

    public static IReadOnlyDictionary<string, FlowTypeDescriptor> InputMap(IReadOnlyDictionary<string, InputDef>? inputs) =>
        inputs?.ToDictionary(static pair => pair.Key, pair => FromInputDef(pair.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, FlowTypeDescriptor>(StringComparer.Ordinal);

    public static FlowTypeDescriptor FromJsonSchema(JsonNode? schema)
    {
        if (schema == null)
            return FlowTypeDescriptor.Any;

        if (schema is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return FromTypeName(text);

            return FlowTypeDescriptor.Any;
        }

        if (schema is not JsonObject obj)
            return FlowTypeDescriptor.Any;

        if (obj["x-gnougo-opaque"] is JsonValue opaqueValue
            && opaqueValue.TryGetValue<bool>(out var opaque)
            && opaque)
        {
            return FlowTypeDescriptor.Any;
        }

        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (obj[keyword] is JsonArray variants)
                return FlowTypeDescriptor.Union(variants.Select(FromJsonSchema));
        }

        if (obj["type"] is JsonArray typeArray)
            return FlowTypeDescriptor.Union(typeArray.Select(FromJsonTypeNode));

        var type = obj.ContainsKey("type") && obj["type"] == null
            ? "null"
            : ReadString(obj["type"]) ?? InferJsonSchemaType(obj);
        var descriptor = FromTypeName(type);

        if (descriptor.Kind == FlowTypeKind.String && obj["enum"] is JsonArray enumArray)
        {
            descriptor = descriptor with
            {
                EnumValues = enumArray
                    .Select(static node => node is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
            };
        }

        if (descriptor.Kind == FlowTypeKind.Array)
            return descriptor with { Items = FromJsonSchema(obj["items"]) };

        if (descriptor.Kind is FlowTypeKind.Object or FlowTypeKind.Dictionary)
        {
            var required = ReadStringArray(obj["required"]);
            required.UnionWith(ReadStringArray(obj["required_properties"]));
            var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
            if (obj["properties"] is JsonObject propertyObject)
            {
                foreach (var (name, child) in propertyObject)
                    properties[name] = new FlowPropertyDescriptor(FromJsonSchema(child), required.Contains(name));
            }

            var additional = obj["additionalProperties"];
            var allowsAdditional = true;
            FlowTypeDescriptor? additionalType = null;
            if (additional is JsonValue additionalValue && additionalValue.TryGetValue<bool>(out var additionalBoolean))
            {
                allowsAdditional = additionalBoolean;
                additionalType = additionalBoolean ? FlowTypeDescriptor.Any : null;
            }
            else if (additional is JsonObject)
            {
                allowsAdditional = true;
                additionalType = FromJsonSchema(additional);
            }

            return descriptor.Kind == FlowTypeKind.Dictionary
                ? FlowTypeDescriptor.Dictionary(additionalType ?? FlowTypeDescriptor.Any)
                : FlowTypeDescriptor.Object(properties, allowsAdditional, additionalType);
        }

        if (descriptor.Kind is FlowTypeKind.Number or FlowTypeKind.Integer
            && TryReadDecimal(obj["minimum"], out var minimum))
        {
            descriptor = descriptor with { Minimum = minimum };
        }

        return descriptor;
    }

    public static JsonObject ToRuntimeJsonSchema(FlowTypeDescriptor descriptor) =>
        ToJsonSchema(descriptor, anyAsOpaqueExtension: true, emitClosedObjects: true);

    public static JsonObject ToPublicJsonSchema(FlowTypeDescriptor descriptor) =>
        ToJsonSchema(descriptor, anyAsOpaqueExtension: false, emitClosedObjects: false);

    public static JsonNode ToWorkflowContractNode(
        FlowTypeDescriptor descriptor,
        bool inputStyle,
        bool allowScalarShortForm = true)
    {
        if (allowScalarShortForm && CanUseScalarWorkflowContract(descriptor))
            return JsonValue.Create(ToWorkflowTypeName(descriptor.Kind))!;

        if (descriptor.Kind == FlowTypeKind.Union)
            return new JsonObject
            {
                ["type"] = "any",
                ["any_of"] = new JsonArray(descriptor.Variants
                    .Select(variant => (JsonNode?)ToWorkflowContractNode(variant, inputStyle))
                    .ToArray())
            };

        var obj = new JsonObject
        {
            ["type"] = ToWorkflowTypeName(descriptor.Kind)
        };
        if (!string.IsNullOrWhiteSpace(descriptor.Description))
            obj["description"] = descriptor.Description;
        if (inputStyle && descriptor.Default != null)
            obj["default"] = descriptor.Default.DeepClone();
        if (descriptor.Items != null)
            obj["items"] = ToWorkflowContractNode(descriptor.Items, inputStyle);
        if (descriptor.Properties.Count > 0)
        {
            var properties = new JsonObject();
            foreach (var (name, property) in descriptor.Properties)
                properties[name] = ToWorkflowContractNode(property.Type, inputStyle);
            obj["properties"] = properties;

            var requiredProperties = descriptor.Properties
                .Where(static pair => pair.Value.Required)
                .Select(static pair => pair.Key)
                .ToArray();
            if (requiredProperties.Length > 0)
                obj["required_properties"] = new JsonArray(requiredProperties
                    .Select(static name => (JsonNode?)JsonValue.Create(name))
                    .ToArray());
        }
        if (descriptor.AdditionalProperties != null && descriptor.AdditionalProperties.Kind != FlowTypeKind.Any)
            obj["additional_properties"] = ToWorkflowContractNode(descriptor.AdditionalProperties, inputStyle);

        return obj;
    }

    public static IEnumerable<string> EnumerateAllowedPaths(
        string prefix,
        FlowTypeDescriptor? descriptor,
        int depth = 0)
    {
        yield return prefix;

        if (depth >= 4 || descriptor == null || descriptor.IsOpaque)
            yield break;

        if (descriptor.Kind == FlowTypeKind.Union)
        {
            foreach (var nested in descriptor.Variants.SelectMany(variant => EnumerateAllowedPaths(prefix, variant, depth)).Distinct(StringComparer.Ordinal))
                yield return nested;
            yield break;
        }

        if (descriptor.Kind is not (FlowTypeKind.Object or FlowTypeKind.Dictionary))
            yield break;

        foreach (var (propertyName, property) in descriptor.Properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var childPrefix = prefix + "." + propertyName;
            yield return childPrefix;
            foreach (var nestedPath in EnumerateAllowedPaths(childPrefix, property.Type, depth + 1).Skip(1))
                yield return nestedPath;
        }
    }

    private static JsonObject ToJsonSchema(
        FlowTypeDescriptor descriptor,
        bool anyAsOpaqueExtension,
        bool emitClosedObjects)
    {
        if (descriptor.Kind == FlowTypeKind.Any)
        {
            var anySchema = anyAsOpaqueExtension ? new JsonObject { ["x-gnougo-opaque"] = true } : new JsonObject();
            if (!string.IsNullOrWhiteSpace(descriptor.Description))
                anySchema["description"] = descriptor.Description;
            if (descriptor.Default != null)
                anySchema["default"] = descriptor.Default.DeepClone();
            return anySchema;
        }

        if (descriptor.Kind == FlowTypeKind.Union)
        {
            return new JsonObject
            {
                ["anyOf"] = new JsonArray(descriptor.Variants
                    .Select(variant => (JsonNode?)ToJsonSchema(variant, anyAsOpaqueExtension, emitClosedObjects))
                    .ToArray())
            };
        }

        var schema = new JsonObject
        {
            ["type"] = ToJsonTypeName(descriptor.Kind)
        };

        if (!string.IsNullOrWhiteSpace(descriptor.Description))
            schema["description"] = descriptor.Description;
        if (descriptor.Default != null)
            schema["default"] = descriptor.Default.DeepClone();
        if (descriptor.Minimum != null)
            schema["minimum"] = JsonValue.Create(descriptor.Minimum.Value);
        if (descriptor.EnumValues.Count > 0)
            schema["enum"] = new JsonArray(descriptor.EnumValues
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray());

        switch (descriptor.Kind)
        {
            case FlowTypeKind.Array:
                schema["items"] = ToJsonSchema(descriptor.Items ?? FlowTypeDescriptor.Any, anyAsOpaqueExtension, emitClosedObjects);
                break;
            case FlowTypeKind.Object:
            case FlowTypeKind.Dictionary:
                if (descriptor.Properties.Count > 0)
                {
                    var properties = new JsonObject();
                    var required = new JsonArray();
                    foreach (var (name, property) in descriptor.Properties)
                    {
                        properties[name] = ToJsonSchema(property.Type, anyAsOpaqueExtension, emitClosedObjects);
                        if (property.Required)
                            required.Add((JsonNode?)JsonValue.Create(name));
                    }

                    schema["properties"] = properties;
                    if (required.Count > 0)
                        schema["required"] = required;
                }
                else if (descriptor.Kind == FlowTypeKind.Object)
                {
                    schema["properties"] = new JsonObject();
                }

                if (descriptor.AdditionalProperties != null)
                    schema["additionalProperties"] = ToJsonSchema(descriptor.AdditionalProperties, anyAsOpaqueExtension, emitClosedObjects);
                else if (emitClosedObjects)
                    schema["additionalProperties"] = descriptor.AllowsAdditionalProperties;
                break;
        }

        return schema;
    }

    private static FlowTypeDescriptor FromWorkflowSchemaParts(
        string type,
        FlowTypeDescriptor? items,
        IReadOnlyDictionary<string, FlowPropertyDescriptor>? properties,
        FlowTypeDescriptor? additionalProperties)
    {
        return type switch
        {
            "string" => FlowTypeDescriptor.String,
            "number" => FlowTypeDescriptor.Number,
            "integer" => FlowTypeDescriptor.Integer,
            "boolean" => FlowTypeDescriptor.Boolean,
            "array" => FlowTypeDescriptor.Array(items),
            "object" => FlowTypeDescriptor.Object(
                properties,
                allowsAdditionalProperties: additionalProperties != null,
                additionalProperties),
            "dictionary" => FlowTypeDescriptor.Dictionary(additionalProperties),
            "null" => FlowTypeDescriptor.Null,
            _ => FlowTypeDescriptor.Any
        };
    }

    private static FlowTypeDescriptor FromJsonTypeNode(JsonNode? node)
    {
        if (node == null)
            return FlowTypeDescriptor.Null;

        return ReadString(node) is { } type ? FromTypeName(type) : FlowTypeDescriptor.Any;
    }

    private static FlowTypeDescriptor FromTypeName(string? type) =>
        NormalizeWorkflowType(type) switch
        {
            "string" => FlowTypeDescriptor.String,
            "number" => FlowTypeDescriptor.Number,
            "integer" => FlowTypeDescriptor.Integer,
            "boolean" => FlowTypeDescriptor.Boolean,
            "array" => FlowTypeDescriptor.Array(),
            "object" => FlowTypeDescriptor.Object(allowsAdditionalProperties: true),
            "dictionary" => FlowTypeDescriptor.Dictionary(),
            "null" => FlowTypeDescriptor.Null,
            _ => FlowTypeDescriptor.Any
        };

    private static string NormalizeWorkflowType(string? type) => type?.ToLowerInvariant() switch
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

    private static string ToJsonTypeName(FlowTypeKind kind) => kind switch
    {
        FlowTypeKind.Null => "null",
        FlowTypeKind.String => "string",
        FlowTypeKind.Number => "number",
        FlowTypeKind.Integer => "integer",
        FlowTypeKind.Boolean => "boolean",
        FlowTypeKind.Array => "array",
        FlowTypeKind.Object or FlowTypeKind.Dictionary => "object",
        _ => "object"
    };

    private static string ToWorkflowTypeName(FlowTypeKind kind) => kind switch
    {
        FlowTypeKind.Null => "null",
        FlowTypeKind.String => "string",
        FlowTypeKind.Number => "number",
        FlowTypeKind.Integer => "integer",
        FlowTypeKind.Boolean => "boolean",
        FlowTypeKind.Array => "array",
        FlowTypeKind.Object => "object",
        FlowTypeKind.Dictionary => "dictionary",
        _ => "any"
    };

    private static bool CanUseScalarWorkflowContract(FlowTypeDescriptor descriptor) =>
        descriptor.Kind is FlowTypeKind.String or FlowTypeKind.Number or FlowTypeKind.Integer or FlowTypeKind.Boolean or FlowTypeKind.Null or FlowTypeKind.Any
        && descriptor.Default == null
        && string.IsNullOrWhiteSpace(descriptor.Description)
        && descriptor.EnumValues.Count == 0
        && descriptor.Minimum == null;

    private static string? InferJsonSchemaType(JsonObject obj)
    {
        if (obj.ContainsKey("properties") || obj.ContainsKey("required") || obj.ContainsKey("additionalProperties"))
            return "object";
        if (obj.ContainsKey("items"))
            return "array";
        if (obj.ContainsKey("enum"))
            return "string";

        return null;
    }

    private static HashSet<string> ReadStringArray(JsonNode? node)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (node is not JsonArray array)
            return values;

        foreach (var item in array)
        {
            if (ReadString(item) is { } value)
                values.Add(value);
        }

        return values;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;

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
        if (jsonValue.TryGetValue<double>(out var doubleValue)
            && !double.IsNaN(doubleValue)
            && !double.IsInfinity(doubleValue))
        {
            value = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }
}
