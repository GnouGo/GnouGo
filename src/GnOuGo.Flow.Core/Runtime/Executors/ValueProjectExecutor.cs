using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Checks a whole value or projects the first present path, optionally for each array item.</summary>
public sealed class ValueProjectExecutor : IStepExecutor
{
    public StepRecovery Recovery => StepRecovery.ReplaySafe;
    public string StepType => "value.project";
    public StepContract Contract => new(
        JsonNode.Parse("""{"type":"object","description":"Select the first present property path from value, or from each array item when each is true. Explicit null counts as present; an empty path checks the whole value. Requires output_schema for the result object containing value.","required":["value","paths"],"additionalProperties":false,"properties":{"value":{},"paths":{"type":"array","minItems":1,"items":{"type":"array","items":{"type":"string"}}},"each":{"type":"boolean"}}}""")!.AsObject(),
        JsonNode.Parse("""{"type":"object","required":["value"],"additionalProperties":false,"properties":{"value":{}}}""")!.AsObject(), InputRequired: true);

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = ctx.Engine.GetResolvedInput(ctx);
        var schema = ctx.Step.Source.OutputSchema as JsonObject;
        if (schema is null || JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: false).Count > 0 ||
            JsonSchemaContractValidator.ValidateInstance(input, Contract.InputSchema).Count > 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Value projection requires a value, paths and a valid output_schema.");
        JsonNode? selected;
        if (input!["each"]?.GetValue<bool>() == true)
        {
            if (input["value"] is not JsonArray items)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Per-item projection requires an array value.");
            var values = new JsonArray();
            foreach (var item in items) values.Add(Select(item));
            selected = values;
        }
        else selected = Select(input["value"]);
        var result = new JsonObject { ["value"] = selected };
        if (JsonSchemaContractValidator.ValidateInstance(result, schema).Count > 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "The selected value does not satisfy the declared output contract.");
        return Task.FromResult<JsonNode?>(result);

        JsonNode? Select(JsonNode? value)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var path in input["paths"]!.AsArray())
            {
                var current = value; var present = true;
                foreach (var part in path!.AsArray())
                {
                    if (current is JsonObject obj && obj.TryGetPropertyValue(part!.GetValue<string>(), out current)) continue;
                    present = false; break;
                }
                if (present) return current?.DeepClone();
            }
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "No declared projection path was present.");
        }
    }
}
