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
        => FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.InputsObject(inputs));

    /// <summary>
    /// Converts a single <see cref="InputDef"/> to a JSON Schema node.
    /// </summary>
    public static JsonNode InputDefToSchema(InputDef def)
        => FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.FromInputDef(def));

    // ── Outputs → JSON Schema ──

    /// <summary>
    /// Generates a JSON Schema "object" from a workflow's output definitions.
    /// </summary>
    public static JsonNode OutputsToJsonSchema(Dictionary<string, OutputDef> outputs)
        => FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.OutputsObject(outputs));

    /// <summary>
    /// Converts a single <see cref="OutputDef"/> to a JSON Schema node.
    /// </summary>
    public static JsonNode OutputDefToSchema(OutputDef def)
        => FlowTypeDescriptorConverter.ToPublicJsonSchema(FlowTypeDescriptorConverter.FromOutputDef(def));
}
