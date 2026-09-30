using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Shared task contract used by planning, adapters and execution.</summary>
public static class AgentTaskContracts
{
    public static JsonObject InputSchema => JsonNode.Parse("""
        {"type":"object","required":["runner","objective","output_schema","workspace","capabilities","budget","verification"],
         "additionalProperties":false,"properties":{
          "runner":{"type":"string","minLength":1},"objective":{"type":"string","minLength":1},
          "inputs":{"type":"object"},"output_schema":{"type":"object"},"workspace":{"type":"string","minLength":1},
          "capabilities":{"type":"array","items":{"type":"string"},"uniqueItems":true},
          "budget":{"type":"object","additionalProperties":false,"required":["max_elapsed_milliseconds","max_model_calls","max_total_tokens"],
            "properties":{"max_elapsed_milliseconds":{"type":"integer","minimum":1},"max_model_calls":{"type":"integer","minimum":1},"max_total_tokens":{"type":"integer","minimum":1}}},
          "verification":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,
            "required":["id","kind","subject","facts_schema"],"properties":{"id":{"type":"string"},"kind":{"type":"string"},
              "subject":{"type":"string"},"facts_schema":{"type":"object"}}}}}}
        """)!.AsObject();

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
