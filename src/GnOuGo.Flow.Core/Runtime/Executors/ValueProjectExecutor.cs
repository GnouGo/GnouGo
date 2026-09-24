using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Projects the first present path from a union value and validates the selected result.</summary>
public sealed class ValueProjectExecutor : IStepExecutor
{
    public string StepType => "value.project";
    public StepContract Contract => new(
        JsonNode.Parse("""{"type":"object","description":"Select the first present property path from value. Explicit null counts as present. Requires output_schema for the result object containing value.","required":["value","paths"],"additionalProperties":false,"properties":{"value":{},"paths":{"type":"array","minItems":1,"items":{"type":"array","items":{"type":"string"}}}}}""")!.AsObject(),
        JsonNode.Parse("""{"type":"object","required":["value"],"additionalProperties":false,"properties":{"value":{}}}""")!.AsObject(), InputRequired: true);

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = ctx.Engine.GetResolvedInput(ctx);
        var schema = ctx.Step.Source.OutputSchema as JsonObject;
        if (schema is null || JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: false).Count > 0 ||
            JsonSchemaContractValidator.ValidateInstance(input, Contract.InputSchema).Count > 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Value projection requires a value, paths and a valid output_schema.");
        foreach (var path in input!["paths"]!.AsArray())
        {
            var current = input["value"]; var present = true;
            foreach (var part in path!.AsArray())
            {
                if (current is JsonObject obj && obj.TryGetPropertyValue(part!.GetValue<string>(), out current)) continue;
                present = false; break;
            }
            if (!present) continue;
            var result = new JsonObject { ["value"] = current?.DeepClone() };
            if (JsonSchemaContractValidator.ValidateInstance(result, schema).Count > 0)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "The selected value does not satisfy the declared output contract.");
            return Task.FromResult<JsonNode?>(result);
        }
        throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "No declared projection path was present.");
    }
}
