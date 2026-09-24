using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Core.Runtime;
internal static class SimulatedSchemaSamples
{
    public static JsonNode? Create(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return JsonValue.Create("dry-run");

        if (TryGetFirstSchema(obj, "anyOf", out var anyOfSample)
            || TryGetFirstSchema(obj, "oneOf", out anyOfSample)
            || TryGetFirstSchema(obj, "allOf", out anyOfSample))
        {
            return anyOfSample;
        }

        if (obj["enum"] is JsonArray enumValues && enumValues.Count > 0)
            return enumValues[0]?.DeepClone();

        var type = ReadSchemaType(obj);
        return type switch
        {
            "object" => CreateSampleObject(obj),
            "array" => CreateSampleArray(obj),
            "integer" => JsonValue.Create(1),
            "number" => JsonValue.Create(1.25),
            "boolean" => JsonValue.Create(true),
            "null" => null,
            "string" => JsonValue.Create("dry-run"),
            _ => obj.ContainsKey("properties")
                ? CreateSampleObject(obj)
                : JsonValue.Create("dry-run")
        };
    }

    private static bool TryGetFirstSchema(JsonObject obj, string keyword, out JsonNode? sample)
    {
        sample = null;
        if (obj[keyword] is not JsonArray schemas)
            return false;

        foreach (var schema in schemas.OfType<JsonObject>())
        {
            if (string.Equals(ReadSchemaType(schema), "null", StringComparison.OrdinalIgnoreCase))
                continue;

            sample = Create(schema);
            return true;
        }

        return false;
    }

    private static string? ReadSchemaType(JsonObject obj)
    {
        if (obj["type"] is JsonValue value)
            return value.GetValue<string>();

        if (obj["type"] is JsonArray types)
        {
            foreach (var typeNode in types.OfType<JsonValue>())
            {
                var type = typeNode.GetValue<string>();
                if (!string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
                    return type;
            }
        }

        return null;
    }

    private static JsonObject CreateSampleObject(JsonObject schema)
    {
        var obj = new JsonObject();
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, propertySchema) in properties)
                obj[name] = Create(propertySchema);
        }
        return obj;
    }

    private static JsonArray CreateSampleArray(JsonObject schema)
    {
        var array = new JsonArray();
        array.Add(Create(schema["items"]));
        return array;
    }

}
