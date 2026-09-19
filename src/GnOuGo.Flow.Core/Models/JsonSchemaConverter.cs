using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

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
        var schema = FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.InputsObject(inputs));
        foreach (var (name, definition) in inputs) schema["properties"]![name] = InputDefToSchema(definition);
        return schema;
    }

    /// <summary>
    /// Converts a single <see cref="InputDef"/> to a JSON Schema node.
    /// </summary>
    public static JsonNode InputDefToSchema(InputDef def)
    {
        if (def.Schema is not null) return def.Schema.DeepClone();
        var schema = FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.FromInputDef(def));
        var body = NonNullSchema(schema);
        if (def.Items is not null) body["items"] = InputDefToSchema(def.Items);
        foreach (var (name, child) in def.Properties ?? []) body["properties"]![name] = InputDefToSchema(child);
        if (def.AdditionalProperties is not null) body["additionalProperties"] = InputDefToSchema(def.AdditionalProperties);
        return schema;
    }

    // ── Outputs → JSON Schema ──

    /// <summary>
    /// Generates a JSON Schema "object" from a workflow's output definitions.
    /// </summary>
    public static JsonNode OutputsToJsonSchema(Dictionary<string, OutputDef> outputs)
    {
        var schema = FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.OutputsObject(outputs));
        foreach (var (name, definition) in outputs) schema["properties"]![name] = OutputDefToSchema(definition);
        return schema;
    }

    /// <summary>
    /// Converts a single <see cref="OutputDef"/> to a JSON Schema node.
    /// </summary>
    public static JsonNode OutputDefToSchema(OutputDef def) => OutputDefToSchema(def, runtime: false);

    internal static JsonNode OutputDefToSchema(OutputDef def, bool runtime)
    {
        if (def.Schema is not null) return def.Schema.DeepClone();
        var descriptor = FlowTypeDescriptorConverter.FromOutputDef(def);
        var schema = runtime ? FlowTypeDescriptorConverter.ToRuntimeJsonSchema(descriptor) : FlowTypeDescriptorConverter.ToPublicJsonSchema(descriptor);
        var body = NonNullSchema(schema);
        if (def.Items is not null) body["items"] = OutputDefToSchema(def.Items, runtime);
        foreach (var (name, child) in def.Properties ?? []) body["properties"]![name] = OutputDefToSchema(child, runtime);
        if (def.AdditionalProperties is not null) body["additionalProperties"] = OutputDefToSchema(def.AdditionalProperties, runtime);
        return schema;
    }
    private static JsonObject NonNullSchema(JsonObject schema) => schema["anyOf"] is JsonArray variants
        ? variants.OfType<JsonObject>().First(v => v["type"]?.ToString() != "null") : schema;
}
