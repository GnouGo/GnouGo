using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Models;

/// <summary>
/// Converts <see cref="InputDef"/> and <see cref="OutputDef"/> type schemas
/// to standard JSON Schema objects. Useful for exposing workflows as MCP tools.
/// </summary>
public static class JsonSchemaConverter
{
    // ── Inputs → JSON Schema ──

    /// <summary>
    /// Generates a JSON Schema "object" from a workflow's input definitions.
    /// </summary>
    public static JsonNode InputsToJsonSchema(Dictionary<string, InputDef> inputs)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, def) in inputs)
        {
            properties[name] = InputDefToSchema(def);
            if (def.Required)
                required.Add(name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    /// <summary>
    /// Converts a single <see cref="InputDef"/> to a JSON Schema node.
    /// </summary>
    public static JsonNode InputDefToSchema(InputDef def)
    {
        var schema = new JsonObject();
        MapBaseType(schema, def.Type);

        if (def.Description != null)
            schema["description"] = def.Description;
        if (def.Default != null)
            schema["default"] = JsonValue.Create(def.Default.ToString());

        // Array items
        if (def.Items != null)
            schema["items"] = InputDefToSchema(def.Items);

        // Object properties
        if (def.Properties != null)
        {
            var props = new JsonObject();
            var reqProps = new JsonArray();
            foreach (var (key, propDef) in def.Properties)
            {
                props[key] = InputDefToSchema(propDef);
                if (propDef.Required)
                    reqProps.Add(key);
            }
            schema["properties"] = props;
            if (def.RequiredProperties is { Count: > 0 })
            {
                var reqList = new JsonArray();
                foreach (var rp in def.RequiredProperties)
                    reqList.Add(rp);
                schema["required"] = reqList;
            }
            else if (reqProps.Count > 0)
            {
                schema["required"] = reqProps;
            }
        }

        // Additional properties (dictionary / extra object props)
        if (def.AdditionalProperties != null)
            schema["additionalProperties"] = InputDefToSchema(def.AdditionalProperties);

        return schema;
    }

    // ── Outputs → JSON Schema ──

    /// <summary>
    /// Generates a JSON Schema "object" from a workflow's output definitions.
    /// </summary>
    public static JsonNode OutputsToJsonSchema(Dictionary<string, OutputDef> outputs)
    {
        var properties = new JsonObject();

        foreach (var (name, def) in outputs)
            properties[name] = OutputDefToSchema(def);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
    }

    /// <summary>
    /// Converts a single <see cref="OutputDef"/> to a JSON Schema node.
    /// </summary>
    public static JsonNode OutputDefToSchema(OutputDef def)
    {
        var schema = new JsonObject();
        MapBaseType(schema, def.Type);

        if (def.Description != null)
            schema["description"] = def.Description;

        // Array items
        if (def.Items != null)
            schema["items"] = OutputDefToSchema(def.Items);

        // Object properties
        if (def.Properties != null)
        {
            var props = new JsonObject();
            foreach (var (key, propDef) in def.Properties)
                props[key] = OutputDefToSchema(propDef);
            schema["properties"] = props;

            if (def.RequiredProperties is { Count: > 0 })
            {
                var reqList = new JsonArray();
                foreach (var rp in def.RequiredProperties)
                    reqList.Add(rp);
                schema["required"] = reqList;
            }
        }

        // Additional properties (dictionary / extra object props)
        if (def.AdditionalProperties != null)
            schema["additionalProperties"] = OutputDefToSchema(def.AdditionalProperties);

        return schema;
    }

    // ── Helpers ──

    private static void MapBaseType(JsonObject schema, string type)
    {
        switch (type.ToLowerInvariant())
        {
            case "string":
                schema["type"] = "string";
                break;
            case "number":
                schema["type"] = "number";
                break;
            case "boolean":
                schema["type"] = "boolean";
                break;
            case "array":
                schema["type"] = "array";
                break;
            case "object":
            case "dictionary":
                schema["type"] = "object";
                break;
            case "any":
            default:
                // JSON Schema: no "type" constraint means any value is accepted.
                break;
        }
    }
}

