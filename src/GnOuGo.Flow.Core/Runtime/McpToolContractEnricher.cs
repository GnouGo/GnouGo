using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

public static class McpToolContractEnricher
{
    private static readonly Regex ResponseFieldRegex = new(
        @"\bresponse\.([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<McpToolInfo> EnrichTools(IReadOnlyList<McpToolInfo> tools)
    {
        var enriched = new List<McpToolInfo>(tools.Count);
        var changed = false;

        foreach (var tool in tools)
        {
            var next = EnrichTool(tool);
            enriched.Add(next);
            changed |= !ReferenceEquals(next, tool);
        }

        return changed ? enriched.AsReadOnly() : tools;
    }

    public static McpToolInfo EnrichTool(McpToolInfo tool)
    {
        if (tool.OutputContract is { } declaredResolution)
        {
            var schema = tool.OutputSchema ?? declaredResolution.Schema;
            var validated = ResolveOutputContract(
                schema,
                declaredResolution.Source ?? string.Empty,
                declaredResolution.Authoritative);
            var errors = validated.Errors
                .Concat(declaredResolution.Errors ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (declaredResolution.Schema != null
                && tool.OutputSchema != null
                && !JsonNode.DeepEquals(declaredResolution.Schema, tool.OutputSchema))
            {
                errors.Add("MCP output contract schema does not match OutputSchema.");
            }

            return CloneWithOutputContract(
                tool,
                validated with
                {
                    Authoritative = validated.Authoritative && errors.Count == 0,
                    Errors = errors.AsReadOnly()
                });
        }

        if (tool.OutputSchema != null)
        {
            return CloneWithOutputContract(
                tool,
                ResolveOutputContract(
                    tool.OutputSchema,
                    McpOutputContractSources.ProtocolSchema,
                    authoritative: true));
        }

        var inferredFromExample = InferOutputSchemaFromExample(tool.ExampleResponse);
        var inferredOutputSchema = inferredFromExample
                                   ?? InferOutputSchemaFromDocumentedResponseFields(tool);
        if (inferredOutputSchema == null)
            return tool;

        var source = inferredFromExample != null
            ? McpOutputContractSources.Example
            : McpOutputContractSources.Description;
        return CloneWithOutputContract(
            tool,
            ResolveOutputContract(inferredOutputSchema, source, authoritative: false));
    }

    public static JsonNode? GetAuthoritativeOutputSchema(McpToolInfo tool)
    {
        var enriched = EnrichTool(tool);
        return enriched.OutputContract is { Authoritative: true, Errors: { Count: 0 }, Schema: not null } contract
            ? contract.Schema
            : null;
    }

    public static McpOutputContractResolution ResolveOutputContract(
        JsonNode? schema,
        string source,
        bool authoritative)
    {
        var errors = new List<string>();
        if (source is not (McpOutputContractSources.ProtocolSchema
            or McpOutputContractSources.Example
            or McpOutputContractSources.Description))
        {
            errors.Add($"Unsupported MCP output contract source '{source}'.");
        }
        ValidateSchemaNode(schema, "$", errors);
        return new McpOutputContractResolution(
            schema?.DeepClone(),
            source,
            authoritative
            && string.Equals(source, McpOutputContractSources.ProtocolSchema, StringComparison.Ordinal)
            && errors.Count == 0,
            errors);
    }

    private static McpToolInfo CloneWithOutputContract(
        McpToolInfo tool,
        McpOutputContractResolution resolution)
    {
        return new McpToolInfo
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema?.DeepClone(),
            Meta = tool.Meta?.DeepClone(),
            OutputSchema = resolution.Schema?.DeepClone(),
            ExampleResponse = tool.ExampleResponse?.DeepClone(),
            ArtifactContract = tool.ArtifactContract,
            CompositionContract = tool.CompositionContract,
            OutputContract = resolution
        };
    }

    private static void ValidateSchemaNode(JsonNode? schema, string path, List<string> errors)
    {
        if (schema is JsonValue booleanSchema && booleanSchema.TryGetValue<bool>(out _))
            return;
        if (schema is not JsonObject obj)
        {
            errors.Add($"{path} must be a JSON Schema object.");
            return;
        }

        if (obj.TryGetPropertyValue("type", out var typeNode) && typeNode != null)
        {
            var validType = typeNode is JsonValue typeValue
                            && typeValue.TryGetValue<string>(out var type)
                            && IsSupportedJsonSchemaType(type)
                            || typeNode is JsonArray typeArray
                            && typeArray.Count > 0
                            && typeArray.All(static item => item is JsonValue value
                                                            && value.TryGetValue<string>(out var itemType)
                                                            && IsSupportedJsonSchemaType(itemType));
            if (!validType)
                errors.Add($"{path}.type must be a supported JSON Schema type or non-empty type array.");
        }

        foreach (var unionName in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!obj.TryGetPropertyValue(unionName, out var unionNode) || unionNode == null)
                continue;
            if (unionNode is not JsonArray variants || variants.Count == 0)
            {
                errors.Add($"{path}.{unionName} must be a non-empty array.");
                continue;
            }
            for (var index = 0; index < variants.Count; index++)
                ValidateSchemaNode(variants[index], $"{path}.{unionName}[{index}]", errors);
        }

        foreach (var definitionsName in new[] { "$defs", "definitions" })
        {
            if (!obj.TryGetPropertyValue(definitionsName, out var definitionsNode) || definitionsNode == null)
                continue;
            if (definitionsNode is not JsonObject definitions)
            {
                errors.Add($"{path}.{definitionsName} must be an object.");
                continue;
            }
            foreach (var (name, definition) in definitions)
                ValidateSchemaNode(definition, $"{path}.{definitionsName}.{name}", errors);
        }

        JsonObject? properties = null;
        if (obj.TryGetPropertyValue("properties", out var propertiesNode))
        {
            if (propertiesNode is not JsonObject declaredProperties)
            {
                errors.Add($"{path}.properties must be an object.");
            }
            else
            {
                properties = declaredProperties;
                foreach (var (name, propertySchema) in properties)
                    ValidateSchemaNode(propertySchema, $"{path}.properties.{name}", errors);
            }
        }

        if (obj.TryGetPropertyValue("required", out var requiredNode))
        {
            if (requiredNode is not JsonArray required)
            {
                errors.Add($"{path}.required must be an array.");
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < required.Count; index++)
                {
                    if (required[index] is not JsonValue value
                        || !value.TryGetValue<string>(out var name)
                        || string.IsNullOrWhiteSpace(name))
                    {
                        errors.Add($"{path}.required[{index}] must be a non-empty string.");
                        continue;
                    }
                    if (!seen.Add(name))
                        errors.Add($"{path}.required[{index}] duplicates property '{name}'.");
                    else if (properties == null || !properties.ContainsKey(name))
                        errors.Add($"{path}.required[{index}] references undeclared property '{name}'.");
                }
            }
        }

        if (obj.TryGetPropertyValue("items", out var itemsNode) && itemsNode != null)
            ValidateSchemaNode(itemsNode, $"{path}.items", errors);
        if (obj.TryGetPropertyValue("additionalProperties", out var additionalProperties)
            && additionalProperties is JsonObject)
        {
            ValidateSchemaNode(additionalProperties, $"{path}.additionalProperties", errors);
        }
    }

    private static bool IsSupportedJsonSchemaType(string type)
        => type is "null" or "boolean" or "object" or "array" or "number" or "integer" or "string";

    private static JsonNode? InferOutputSchemaFromDocumentedResponseFields(McpToolInfo tool)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        AddDocumentedResponseFields(fields, tool.Description);
        AddSchemaDocumentedResponseFields(fields, tool.InputSchema);

        if (fields.Count == 0)
            return null;

        var properties = new JsonObject();
        foreach (var (fieldName, description) in fields.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            properties[fieldName] = BuildStringPropertySchema(fieldName, description);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = true
        };
    }

    private static JsonNode? InferOutputSchemaFromExample(JsonNode? example)
    {
        if (example is not JsonObject obj)
            return null;

        return BuildSchemaFromExampleObject(obj);
    }

    private static JsonObject BuildSchemaFromExampleObject(JsonObject obj)
    {
        var properties = new JsonObject();
        foreach (var (name, value) in obj)
            properties[name] = BuildSchemaFromExampleValue(name, value);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = true
        };
    }

    private static JsonObject BuildSchemaFromExampleValue(string fieldName, JsonNode? value)
    {
        JsonObject schema;
        if (value is JsonObject objectValue)
        {
            schema = BuildSchemaFromExampleObject(objectValue);
        }
        else if (value is JsonArray arrayValue)
        {
            schema = new JsonObject
            {
                ["type"] = "array"
            };
            if (arrayValue.FirstOrDefault() is { } first)
                schema["items"] = BuildSchemaFromExampleValue(fieldName, first);
        }
        else if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _))
        {
            schema = new JsonObject { ["type"] = "string" };
        }
        else if (value is JsonValue boolValue && boolValue.TryGetValue<bool>(out _))
        {
            schema = new JsonObject { ["type"] = "boolean" };
        }
        else if (value is JsonValue numberValue && numberValue.TryGetValue<decimal>(out _))
        {
            schema = new JsonObject { ["type"] = "number" };
        }
        else
        {
            schema = new JsonObject();
        }

        return schema;
    }

    private static JsonObject BuildStringPropertySchema(string fieldName, string description)
        => new()
        {
            ["type"] = "string",
            ["description"] = description
        };

    private static void AddSchemaDocumentedResponseFields(Dictionary<string, string> fields, JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return;

        AddDocumentedResponseFields(fields, ReadString(obj, "description"));
        AddDocumentedResponseFields(fields, ReadString(obj, "title"));

        foreach (var variantName in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (obj[variantName] is not JsonArray variants)
                continue;
            foreach (var variant in variants)
                AddSchemaDocumentedResponseFields(fields, variant);
        }

        if (obj["properties"] is JsonObject properties)
        {
            foreach (var (_, propertySchema) in properties)
                AddSchemaDocumentedResponseFields(fields, propertySchema);
        }

        AddSchemaDocumentedResponseFields(fields, obj["items"]);
    }

    private static void AddDocumentedResponseFields(Dictionary<string, string> fields, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in ResponseFieldRegex.Matches(text))
        {
            var fieldName = match.Groups[1].Value;
            if (!fields.ContainsKey(fieldName))
                fields[fieldName] = text;
        }
    }

    private static string? ReadString(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
