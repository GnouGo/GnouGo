using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Shared task contract used by planning, adapters and execution.</summary>
public static class AgentTaskContracts
{
    public static string McpToolId(JsonObject descriptor)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream)) WriteOrdered(descriptor, writer);
        return "mcp_" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void WriteOrdered(JsonNode? node, System.Text.Json.Utf8JsonWriter writer)
    {
        if (node is JsonObject map)
        {
            writer.WriteStartObject();
            foreach (var pair in map.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteOrdered(pair.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray list)
        {
            writer.WriteStartArray();
            foreach (var item in list) WriteOrdered(item, writer);
            writer.WriteEndArray();
        }
        else if (node is null) writer.WriteNullValue();
        else node.WriteTo(writer);
    }

    public static JsonObject InputSchema => JsonNode.Parse("""
        {"type":"object","required":["runner","objective","output_schema","workspace","capabilities","budget","verification"],
         "additionalProperties":false,"properties":{
          "runner":{"type":"string","minLength":1},"objective":{"type":"string","minLength":1},
          "inputs":{"type":"object"},"output_schema":{"type":"object"},"workspace":{"type":"string","minLength":1},
          "capabilities":{"type":"array","items":{"type":"string"},"uniqueItems":true},
          "budget":{"type":"object","additionalProperties":false,"required":["max_elapsed_milliseconds","max_model_calls","max_total_tokens"],
            "properties":{"max_actions":{"type":"integer","minimum":1,"maximum":2147483647},"max_elapsed_milliseconds":{"type":"integer","minimum":1},"max_model_calls":{"type":"integer","minimum":1},"max_total_tokens":{"type":"integer","minimum":1}}},
          "verification":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,
            "required":["id","kind","subject","facts_schema"],"properties":{"id":{"type":"string"},"kind":{"type":"string"},
              "subject":{"type":"string"},"facts_schema":{"type":"object"}}}}}}
        """)!.AsObject();

    internal static JsonObject RunnerInputSchema(bool supportsActionLimit)
    {
        var schema = InputSchema;
        if (!supportsActionLimit)
            schema["properties"]!["budget"]!["properties"]!.AsObject().Remove("max_actions");
        return schema;
    }

    public static AgentTaskDefinition Parse(JsonNode? input)
    {
        if (JsonSchemaContractValidator.ValidateInstance(input, InputSchema).Count != 0)
            throw Invalid();
        var task = JsonSerializer.Deserialize(input, AgentTaskJsonContext.Default.AgentTaskDefinition) ?? throw Invalid();
        if (string.IsNullOrWhiteSpace(task.Runner) || string.IsNullOrWhiteSpace(task.Objective) || string.IsNullOrWhiteSpace(task.Workspace) ||
            task.Capabilities.Any(string.IsNullOrWhiteSpace) ||
            JsonSchemaContractValidator.ValidateSchema(task.OutputSchema, strictProfile: false).Count > 0 ||
            task.Verification.Any(v => string.IsNullOrWhiteSpace(v.Id) || string.IsNullOrWhiteSpace(v.Kind) || string.IsNullOrWhiteSpace(v.Subject) ||
                JsonSchemaContractValidator.ValidateSchema(v.FactsSchema, strictProfile: false).Count > 0) ||
            task.Verification.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != task.Verification.Count)
            throw Invalid();
        return task;
    }
    private static WorkflowRuntimeException Invalid() => new(ErrorCodes.InputValidation,
        "Agent scope, output schema and verification requirements must satisfy the approved contract.", retryable: false);
}
