using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

internal static class McpToolContractEnricher
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
        if (tool.OutputSchema != null)
            return tool;

        var inferredOutputSchema = InferOutputSchemaFromExample(tool.ExampleResponse)
                                   ?? InferOutputSchemaFromDocumentedResponseFields(tool);
        if (inferredOutputSchema == null)
            return tool;

        return new McpToolInfo
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema?.DeepClone(),
            OutputSchema = inferredOutputSchema,
            ExampleResponse = tool.ExampleResponse?.DeepClone()
        };
    }

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
