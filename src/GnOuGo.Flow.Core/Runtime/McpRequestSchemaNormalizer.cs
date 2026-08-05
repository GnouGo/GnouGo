using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Applies JSON-object optionality semantics to resolved MCP requests. A property
/// whose schema does not require it is omitted when its resolved value is JSON
/// null; required properties retain null and fail normal schema validation.
/// </summary>
internal static class McpRequestSchemaNormalizer
{
    public static JsonNode? OmitNullOptionalProperties(JsonNode? request, JsonNode? schema)
    {
        var clone = request?.DeepClone();
        NormalizeNode(clone, schema as JsonObject);
        return clone;
    }

    public static bool IsOptionalPropertyPath(JsonObject schema, string validatorField)
    {
        var path = validatorField.StartsWith("input.", StringComparison.Ordinal)
            ? validatorField["input.".Length..]
            : validatorField;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        JsonObject? current = schema;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var arrayIndex = segment.IndexOf('[');
            if (arrayIndex >= 0)
                segment = segment[..arrayIndex];
            if (string.IsNullOrWhiteSpace(segment) || current == null)
                return false;

            var propertySchema = FindPropertySchema(current, segment);
            if (propertySchema == null)
                return false;
            if (index == segments.Length - 1)
                return !IsAlwaysRequired(current, segment);

            var next = propertySchema;
            if (segments[index].Contains('['))
                next = FindItemsSchema(next);
            current = next;
        }

        return false;
    }

    private static void NormalizeNode(JsonNode? value, JsonObject? schema)
    {
        if (value is JsonObject obj && schema != null)
        {
            foreach (var name in obj.Select(static pair => pair.Key).ToArray())
            {
                var propertySchema = FindPropertySchema(schema, name);
                if (propertySchema == null)
                    continue;

                if (obj[name] == null && !IsAlwaysRequired(schema, name))
                {
                    obj.Remove(name);
                    continue;
                }

                NormalizeNode(obj[name], propertySchema);
            }
            return;
        }

        if (value is JsonArray array && schema != null)
        {
            var itemsSchema = FindItemsSchema(schema);
            if (itemsSchema == null)
                return;
            foreach (var item in array)
                NormalizeNode(item, itemsSchema);
        }
    }

    private static JsonObject? FindPropertySchema(JsonObject schema, string propertyName)
    {
        if (schema["properties"] is JsonObject properties
            && properties[propertyName] is JsonObject direct)
        {
            return direct;
        }

        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (schema[keyword] is not JsonArray branches)
                continue;
            foreach (var branch in branches.OfType<JsonObject>())
            {
                var nested = FindPropertySchema(branch, propertyName);
                if (nested != null)
                    return nested;
            }
        }

        return null;
    }

    private static JsonObject? FindItemsSchema(JsonObject schema)
    {
        if (schema["items"] is JsonObject items)
            return items;
        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (schema[keyword] is not JsonArray branches)
                continue;
            foreach (var branch in branches.OfType<JsonObject>())
            {
                var nested = FindItemsSchema(branch);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    private static bool IsAlwaysRequired(JsonObject schema, string propertyName)
    {
        if (schema["required"] is JsonArray required
            && required.Any(item => item is JsonValue scalar
                                    && scalar.TryGetValue<string>(out var value)
                                    && string.Equals(value, propertyName, StringComparison.Ordinal)))
        {
            return true;
        }

        // allOf requirements apply together. oneOf/anyOf requirements are branch
        // dependent and therefore are not unconditional optionality constraints.
        if (schema["allOf"] is JsonArray allOf)
            return allOf.OfType<JsonObject>().Any(branch => IsAlwaysRequired(branch, propertyName));

        return false;
    }
}
